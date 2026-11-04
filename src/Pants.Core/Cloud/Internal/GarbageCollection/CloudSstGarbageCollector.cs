namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed class CloudSstGarbageCollector
{
    readonly Func<CancellationToken, ValueTask<CloudSstRetentionProof>> _captureProofAsync;
    readonly Action _ensureAuthority;
    readonly IFailpointHandler _failpoints;
    readonly Func<IReadOnlySet<string>> _readLocalProtectedNames;
    readonly ICloudObjectStore _sstStore;
    readonly Func<CancellationToken, ValueTask> _validateRemoteAuthorityAsync;

    public CloudSstGarbageCollector(
        ICloudObjectStore sstStore,
        Func<CancellationToken, ValueTask<CloudSstRetentionProof>> captureProofAsync,
        Func<IReadOnlySet<string>> readLocalProtectedNames,
        Action ensureAuthority,
        Func<CancellationToken, ValueTask> validateRemoteAuthorityAsync,
        IFailpointHandler failpoints)
    {
        _sstStore = sstStore;
        _captureProofAsync = captureProofAsync;
        _readLocalProtectedNames = readLocalProtectedNames;
        _ensureAuthority = ensureAuthority;
        _validateRemoteAuthorityAsync = validateRemoteAuthorityAsync;
        _failpoints = failpoints;
    }

    public async ValueTask<bool> CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ensureAuthority();
            using var proof = await _captureProofAsync(cancellationToken).ConfigureAwait(false);
            var objectKeys = await _sstStore.ListAllAsync(
                PantsCloudObjectLayout.SstPrefix,
                cancellationToken).ConfigureAwait(false);
            var encounteredAnomaly = false;
            foreach (var objectKey in objectKeys.Concat(proof.RetiredSsts?.Keys.Select(static name => PantsCloudObjectLayout.SstPrefix + name) ?? [])
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                _ensureAuthority();
                if (!CloudSstObjectKey.TryGetName(objectKey, out var name))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                var retired = proof.RetiredSsts?.GetValueOrDefault(name);
                var isRetired = retired is not null;
                var isPartialCompactionOutput = proof.PartialCompactionOutputVersions.TryGetValue(
                    name,
                    out var expectedPartialOutputVersion);
                if ((proof.ProtectedNames.Contains(name) && !isPartialCompactionOutput) ||
                    _readLocalProtectedNames().Contains(name))
                {
                    continue;
                }

                if (!isRetired && (!CloudSstIdentity.TryParse(name, out var sstIdentity) ||
                    !proof.RemoteNextSstSequences.TryGetValue(sstIdentity.ColumnFamilyId, out var nextSequence) ||
                    (!isPartialCompactionOutput && sstIdentity.Sequence >= nextSequence)))
                {
                    continue;
                }

                if (isRetired && proof.ValidateRetiredSst?.Invoke(retired!) != true)
                {
                    continue;
                }

                var objectIdentity = await _sstStore.HeadAsync(objectKey, cancellationToken)
                    .ConfigureAwait(false);
                if (objectIdentity is null)
                {
                    if (isRetired && await VerifyProofAsync(proof, cancellationToken).ConfigureAwait(false) &&
                        proof.ValidateRetiredSst?.Invoke(retired!) == true && !_readLocalProtectedNames().Contains(name))
                    {
                        await _validateRemoteAuthorityAsync(cancellationToken).ConfigureAwait(false);
                        _ensureAuthority();
                        proof.AcknowledgeRetiredSst?.Invoke(retired!);
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(objectIdentity.Version))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                if (isPartialCompactionOutput &&
                    !StringComparer.Ordinal.Equals(objectIdentity.Version, expectedPartialOutputVersion))
                {
                    return false;
                }

                if (isRetired && !await VerifyRetiredBytesAsync(proof, objectKey, objectIdentity, retired!, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (!await VerifyProofAsync(proof, cancellationToken).ConfigureAwait(false) ||
                    _readLocalProtectedNames().Contains(name))
                {
                    return false;
                }

                _failpoints.Hit(Failpoint.BeforeCloudSstGarbageCollectionDelete);
                _ensureAuthority();
                await _validateRemoteAuthorityAsync(cancellationToken).ConfigureAwait(false);
                if (isRetired && proof.ValidateRetiredSst?.Invoke(retired!) != true)
                {
                    return false;
                }

                var outcome = await _sstStore.DeleteAsync(
                    objectKey,
                    new PantsCloudObjectDeleteCondition.IfVersion(objectIdentity.Version),
                    cancellationToken).ConfigureAwait(false);
                if (outcome == CloudObjectDeleteOutcome.ConditionNotMet)
                {
                    throw new PantsRecoveryFailedException(
                        $"Cloud SST garbage collection proof for '{objectKey}' became stale before deletion.");
                }

                if (isRetired && outcome is CloudObjectDeleteOutcome.Deleted or CloudObjectDeleteOutcome.NotFound)
                {
                    _ensureAuthority();
                    await _validateRemoteAuthorityAsync(cancellationToken).ConfigureAwait(false);
                    proof.AcknowledgeRetiredSst?.Invoke(retired!);
                }
            }

            return !encounteredAnomaly;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PantsRecoveryFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    async ValueTask<bool> VerifyRetiredBytesAsync(
        CloudSstRetentionProof proof, string objectKey, CloudObjectMetadata identity, FileMeta retired,
        CancellationToken cancellationToken)
    {
        if (retired.SizeBytes == 0 || !retired.ContentCrc32C.HasValue || identity.SizeBytes != retired.SizeBytes)
        {
            return false;
        }

        var budget = proof.ValidationBudget ?? new ResourceBudget(1024 * 1024);
        var pageBytes = checked((int)Math.Min(retired.SizeBytes, (ulong)Math.Min(64 * 1024L, Math.Max(1L, budget.Limit / 8))));
        using var reservation = budget.Reserve(checked(pageBytes * 2L));
        var checksum = 0U;
        for (ulong offset = 0; offset < retired.SizeBytes;)
        {
            _ensureAuthority();
            var length = checked((int)Math.Min((ulong)pageBytes, retired.SizeBytes - offset));
            CloudObject? page;
            try
            {
                page = await _sstStore.GetRangeAsync(objectKey, offset, length, cancellationToken).ConfigureAwait(false);
            }
            catch (PantsNotSupportedException) when (retired.SizeBytes <= (ulong)pageBytes)
            {
                page = await _sstStore.GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
            }

            if (page is null || page.Version != identity.Version || page.Data.Length != length)
            {
                return false;
            }

            checksum = DiskFormat.Crc32CAppend(checksum, page.Data.Span);
            offset += checked((ulong)length);
        }

        var confirmed = await _sstStore.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        return checksum == retired.ContentCrc32C && confirmed?.Version == identity.Version && confirmed.SizeBytes == retired.SizeBytes;
    }

    async ValueTask<bool> VerifyProofAsync(
        CloudSstRetentionProof proof,
        CancellationToken cancellationToken)
    {
        foreach (var guard in proof.AuthorityGuards)
        {
            _ensureAuthority();
            var current = await guard.Store.HeadAsync(guard.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            if (current is null ||
                !StringComparer.Ordinal.Equals(current.Version, guard.Version))
            {
                return false;
            }
        }

        return true;
    }
}
