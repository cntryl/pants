namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed class CloudSstGarbageCollector
{
    readonly Func<CancellationToken, ValueTask<CloudSstRetentionProof>> _captureProofAsync;
    readonly Action _ensureAuthority;
    readonly IFailpointHandler _failpoints;
    readonly Func<IReadOnlySet<string>> _readLocalProtectedNames;
    readonly ICloudObjectStore _sstStore;

    public CloudSstGarbageCollector(
        ICloudObjectStore sstStore,
        Func<CancellationToken, ValueTask<CloudSstRetentionProof>> captureProofAsync,
        Func<IReadOnlySet<string>> readLocalProtectedNames,
        Action ensureAuthority,
        IFailpointHandler failpoints)
    {
        _sstStore = sstStore;
        _captureProofAsync = captureProofAsync;
        _readLocalProtectedNames = readLocalProtectedNames;
        _ensureAuthority = ensureAuthority;
        _failpoints = failpoints;
    }

    public async ValueTask<bool> CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ensureAuthority();
            var proof = await _captureProofAsync(cancellationToken).ConfigureAwait(false);
            var objectKeys = await _sstStore.ListAllAsync(
                PantsCloudObjectLayout.SstPrefix,
                cancellationToken).ConfigureAwait(false);
            var encounteredAnomaly = false;
            foreach (var objectKey in objectKeys
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                _ensureAuthority();
                if (!CloudSstObjectKey.TryGetName(objectKey, out var name))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                if (proof.ProtectedNames.Contains(name) ||
                    _readLocalProtectedNames().Contains(name))
                {
                    continue;
                }

                if (!CloudSstIdentity.TryParse(name, out var sstIdentity) ||
                    !proof.RemoteNextSstSequences.TryGetValue(
                        sstIdentity.ColumnFamilyId,
                        out var nextSequence) ||
                    sstIdentity.Sequence >= nextSequence)
                {
                    // A successor may still allocate this name from the
                    // authoritative manifest. Retention is the only safe
                    // action until every remote manifest advances past it.
                    continue;
                }

                var objectIdentity = await _sstStore.HeadAsync(objectKey, cancellationToken)
                    .ConfigureAwait(false);
                if (objectIdentity is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(objectIdentity.Version))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                if (!await VerifyProofAsync(proof, cancellationToken).ConfigureAwait(false) ||
                    _readLocalProtectedNames().Contains(name))
                {
                    return false;
                }

                _ensureAuthority();
                _failpoints.Hit(Failpoint.BeforeCloudSstGarbageCollectionDelete);
                var outcome = await _sstStore.DeleteAsync(
                    objectKey,
                    new PantsCloudObjectDeleteCondition.IfVersion(objectIdentity.Version),
                    cancellationToken).ConfigureAwait(false);
                if (outcome == CloudObjectDeleteOutcome.ConditionNotMet)
                {
                    throw new PantsRecoveryFailedException(
                        $"Cloud SST garbage collection proof for '{objectKey}' became stale before deletion.");
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
