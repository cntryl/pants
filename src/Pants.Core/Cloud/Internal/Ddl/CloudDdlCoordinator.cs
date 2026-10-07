using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal.Ddl;

sealed class CloudDdlCoordinator
{
    const string PrepareFileName = "ddl.prepare.json";
    readonly ICloudDdlAuthority _authority;
    readonly LocalDiskStore _diskStore;
    readonly IFailpointHandler _failpoints;

    readonly string _preparePath;
    bool _authorityAmbiguous;

    public CloudDdlCoordinator(
        string localRoot,
        ICloudDdlAuthority authority,
        LocalDiskStore diskStore,
        IFailpointHandler failpoints)
    {
        _preparePath = Path.Combine(Path.GetFullPath(localRoot), PrepareFileName);
        _authority = authority;
        _diskStore = diskStore;
        _failpoints = failpoints;
    }

    public void EnsureAuthorityResolved()
    {
        if (_authorityAmbiguous)
        {
            throw new PantsFencedException(
                "Cloud DDL authority is ambiguous and must be reconciled before another write.");
        }
    }

    public async ValueTask ReconcileStartupAsync(
        RuntimeState state,
        CancellationToken cancellationToken)
    {
        await _authority.FenceDdlRegistryAsync(
            FromLocalManifest(),
            cancellationToken).ConfigureAwait(false);
        await ReconcilePreparedAsync(state, cancellationToken, true).ConfigureAwait(false);
        var remote = await _authority.ReadDdlRegistryAsync(cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            var bootstrap = FromLocalManifest();
            if (!await _authority.CompareExchangeDdlRegistryAsync(
                    bootstrap,
                    null,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new PantsFencedException(
                    "Cloud DDL registry bootstrap lost its authority race.");
            }

            _authorityAmbiguous = false;
            return;
        }

        foreach (var operation in remote.Registry.Operations)
        {
            if (!_diskStore.IsColumnFamilyEditApplied(operation.Edit))
            {
                _diskStore.CommitColumnFamilyEdit(state, operation.Edit);
            }
            else
            {
                _diskStore.ApplyColumnFamilyEditVisibility(state, operation.Edit);
            }
        }

        ValidateLocalStateAgainst(remote.Registry);
        _authorityAmbiguous = false;
    }

    public ValueTask ReconcilePendingAsync(
        RuntimeState state,
        CancellationToken cancellationToken) =>
        ReconcilePreparedAsync(state, cancellationToken);

    public async ValueTask ExecuteAsync(
        RuntimeState state,
        JsonElement edit,
        CancellationToken cancellationToken)
    {
        CloudDdlEdit.Validate(edit);
        await ReconcilePreparedAsync(state, cancellationToken).ConfigureAwait(false);
        EnsureAuthorityResolved();
        if (_diskStore.IsColumnFamilyEditApplied(edit))
        {
            _diskStore.ApplyColumnFamilyEditVisibility(state, edit);
            return;
        }

        var remote = await _authority.ReadDdlRegistryAsync(cancellationToken).ConfigureAwait(false);
        var registry = remote?.Registry.Clone() ?? FromLocalManifest();
        var prepare = new CloudDdlPrepare
        {
            OperationId = Guid.NewGuid().ToString(),
            ExpectedRemoteEpoch = registry.Epoch,
            Edit = edit.Clone()
        };
        WritePrepare(prepare);

        CloudDdlEdit.Apply(registry.ColumnFamilies, edit);
        registry.Epoch = registry.Epoch == ulong.MaxValue
            ? ulong.MaxValue
            : registry.Epoch + 1;
        registry.Operations.Add(new CloudDdlOperation
        {
            OperationId = prepare.OperationId,
            Edit = edit.Clone()
        });

        var submitted = false;
        try
        {
            _failpoints.Hit(Failpoint.BeforeDdlRemoteCas);
            // Record that the CAS may land before submitting it, so a crash or a lost response
            // cannot be mistaken for an aborted DDL.
            prepare.RemoteCasAmbiguous = true;
            WritePrepare(prepare);
            submitted = true;
            _failpoints.Hit(Failpoint.AfterDdlAmbiguousPrepare);
            var published = await _authority.CompareExchangeDdlRegistryAsync(
                registry,
                remote?.Version,
                cancellationToken).ConfigureAwait(false);
            if (!published)
            {
                // A rejected conditional write is a definite answer: it did not commit.
                submitted = false;
                throw new PantsFencedException(
                    "Cloud DDL registry publication lost its authority race.");
            }

            _failpoints.Hit(Failpoint.AfterDdlRemoteCas);
        }
        catch (Exception publicationError)
        {
            try
            {
                _failpoints.Hit(Failpoint.BeforeDdlAuthorityReadback);
                var readback = await _authority.ReadDdlRegistryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (readback?.Registry.Operations.Any(operation =>
                        StringComparer.Ordinal.Equals(
                            operation.OperationId,
                            prepare.OperationId)) == true)
                {
                    ApplyRemoteCommittedVisibility(state, edit);
                    return;
                }
            }
            catch (Exception readbackError)
            {
                _authorityAmbiguous = true;
                MarkPersistenceAnomaly(state);
                throw new PantsFencedException(
                    "Cloud DDL authority is ambiguous after a lost registry publication response.",
                    new AggregateException(publicationError, readbackError));
            }

            if (submitted)
            {
                // The CAS was sent and its outcome is unknown; absence now does not prove abort
                // because the store may still apply it. Fence and keep the marker.
                _authorityAmbiguous = true;
                MarkPersistenceAnomaly(state);
                throw new PantsFencedException(
                    "Cloud DDL registry publication has an unknown outcome and may still commit.",
                    publicationError);
            }

            // Proven not committed (never submitted, or definitively rejected).
            prepare.RemoteCasAmbiguous = false;
            TryWritePrepare(prepare);
            throw;
        }

        try
        {
            _diskStore.CommitColumnFamilyEdit(state, edit);
        }
        catch (Exception)
        {
            ApplyRemoteCommittedVisibility(state, edit);
            return;
        }

        TryClearPrepare();
        _authorityAmbiguous = false;
    }

    async ValueTask ReconcilePreparedAsync(
        RuntimeState state,
        CancellationToken cancellationToken,
        bool redriveAmbiguous = false)
    {
        var prepare = ReadPrepare();
        if (prepare is null)
        {
            _authorityAmbiguous = false;
            return;
        }

        var remote = await _authority.ReadDdlRegistryAsync(cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            if (_diskStore.IsColumnFamilyEditApplied(prepare.Edit))
            {
                throw new PantsRecoveryFailedException(
                    "A local cloud DDL commit exists but the remote registry is missing.");
            }

            ClearPrepare();
            _authorityAmbiguous = false;
            return;
        }

        if (remote.Registry.Operations.Any(operation =>
                StringComparer.Ordinal.Equals(operation.OperationId, prepare.OperationId)))
        {
            if (!_diskStore.IsColumnFamilyEditApplied(prepare.Edit))
            {
                _diskStore.CommitColumnFamilyEdit(state, prepare.Edit);
            }
            else
            {
                _diskStore.ApplyColumnFamilyEditVisibility(state, prepare.Edit);
            }

            ClearPrepare();
            _authorityAmbiguous = false;
            return;
        }

        if (_diskStore.IsColumnFamilyEditApplied(prepare.Edit))
        {
            throw new PantsRecoveryFailedException(
                "A local cloud DDL commit is absent from the remote registry.");
        }

        if (prepare.RemoteCasAmbiguous)
        {
            if (!redriveAmbiguous)
            {
                // In process the submitted CAS may still land; keep fencing until it is resolved.
                _authorityAmbiguous = true;
                return;
            }

            await RedriveAmbiguousPrepareAsync(state, prepare, remote, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ClearPrepare();
        _authorityAmbiguous = false;
    }

    /// <summary>
    ///     Re-submits an ambiguous DDL once at startup against the epoch it was prepared at. The same
    ///     operation ID makes a duplicate harmless; a registry that has since advanced means the
    ///     DDL's fate can no longer be decided here, so open fails rather than silently aborting.
    /// </summary>
    async ValueTask RedriveAmbiguousPrepareAsync(
        RuntimeState state,
        CloudDdlPrepare prepare,
        CloudDdlRegistryObject remote,
        CancellationToken cancellationToken)
    {
        if (remote.Registry.Epoch != prepare.ExpectedRemoteEpoch)
        {
            throw new PantsFencedException(
                "A cloud DDL with an unknown outcome was prepared against registry epoch " +
                $"{prepare.ExpectedRemoteEpoch}, but the registry is now at epoch " +
                $"{remote.Registry.Epoch}.");
        }

        var registry = remote.Registry.Clone();
        CloudDdlEdit.Apply(registry.ColumnFamilies, prepare.Edit);
        registry.Epoch = registry.Epoch == ulong.MaxValue
            ? ulong.MaxValue
            : registry.Epoch + 1;
        registry.Operations.Add(new CloudDdlOperation
        {
            OperationId = prepare.OperationId,
            Edit = prepare.Edit.Clone()
        });
        if (!await _authority.CompareExchangeDdlRegistryAsync(
                registry,
                remote.Version,
                cancellationToken).ConfigureAwait(false))
        {
            var current = await _authority.ReadDdlRegistryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current?.Registry.Operations.Any(operation =>
                    StringComparer.Ordinal.Equals(operation.OperationId, prepare.OperationId)) != true)
            {
                throw new PantsFencedException(
                    "Redriving a cloud DDL with an unknown outcome lost its authority race.");
            }
        }

        if (!_diskStore.IsColumnFamilyEditApplied(prepare.Edit))
        {
            _diskStore.CommitColumnFamilyEdit(state, prepare.Edit);
        }
        else
        {
            _diskStore.ApplyColumnFamilyEditVisibility(state, prepare.Edit);
        }

        ClearPrepare();
        _authorityAmbiguous = false;
    }

    CloudDdlRegistry FromLocalManifest() => new()
    {
        ColumnFamilies = _diskStore.GetColumnFamilyMetadataSnapshot()
            .Select(CloudDdlColumnFamily.FromManifest)
            .ToList()
    };

    void ValidateLocalStateAgainst(CloudDdlRegistry registry)
    {
        var localFamilies = _diskStore.GetColumnFamilyMetadataSnapshot();
        foreach (var remoteFamily in registry.ColumnFamilies)
        {
            var localFamily = localFamilies.SingleOrDefault(family =>
                family.Id == remoteFamily.Id);
            if (localFamily is not null &&
                !StringComparer.Ordinal.Equals(localFamily.Name, remoteFamily.Name))
            {
                throw new PantsRecoveryFailedException(
                    $"Cloud DDL registry conflicts for column family {remoteFamily.Id}.");
            }
        }

        if (localFamilies.Any(local =>
                registry.ColumnFamilies.All(remote => remote.Id != local.Id)))
        {
            throw new PantsRecoveryFailedException(
                "Local column-family state is ahead of the cloud DDL registry.");
        }
    }

    CloudDdlPrepare? ReadPrepare() => File.Exists(_preparePath)
        ? CloudDdlJson.DeserializePrepare(File.ReadAllBytes(_preparePath))
        : null;

    void WritePrepare(CloudDdlPrepare prepare)
    {
        _failpoints.Hit(Failpoint.BeforeDdlPrepare);
        AtomicStagedFile.Write(_preparePath, CloudDdlJson.SerializePrepare(prepare));
        _failpoints.Hit(Failpoint.AfterDdlPrepare);
    }

    void TryWritePrepare(CloudDdlPrepare prepare)
    {
        try
        {
            AtomicStagedFile.Write(_preparePath, CloudDdlJson.SerializePrepare(prepare));
        }
        catch (IOException)
        {
            // The marker stays set; startup reconciliation resolves it conservatively.
        }
        catch (UnauthorizedAccessException)
        {
            // The marker stays set; startup reconciliation resolves it conservatively.
        }
    }

    void TryClearPrepare()
    {
        try
        {
            ClearPrepare();
        }
        catch (IOException)
        {
            // A committed prepare is deliberately retained for startup reconciliation.
        }
        catch (UnauthorizedAccessException)
        {
            // A committed prepare is deliberately retained for startup reconciliation.
        }
    }

    void ClearPrepare() => AtomicStagedFile.Delete(_preparePath);

    void ApplyRemoteCommittedVisibility(RuntimeState state, JsonElement edit)
    {
        _diskStore.AdoptRemoteCommittedColumnFamilyEdit(state, edit);
        MarkPersistenceAnomaly(state);
    }

    static void MarkPersistenceAnomaly(RuntimeState state)
    {
        if (state.Health == PantsEngineHealth.Healthy)
        {
            state.Health = PantsEngineHealth.Degraded;
        }
    }
}
