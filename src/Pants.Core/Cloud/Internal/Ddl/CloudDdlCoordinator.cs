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
        await ReconcilePreparedAsync(state, cancellationToken).ConfigureAwait(false);
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

        try
        {
            _failpoints.Hit(Failpoint.BeforeDdlRemoteCas);
            var published = await _authority.CompareExchangeDdlRegistryAsync(
                registry,
                remote?.Version,
                cancellationToken).ConfigureAwait(false);
            if (!published)
            {
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
        CancellationToken cancellationToken)
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
