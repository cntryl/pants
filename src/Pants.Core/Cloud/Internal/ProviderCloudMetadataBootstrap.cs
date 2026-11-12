using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>Publishes the first provider generation without making a local cache authoritative.</summary>
static class ProviderCloudMetadataBootstrap
{
    const int MaximumFenceAttempts = 8;
    const int MaximumInventoryPages = 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async ValueTask<ProviderCloudHydrationResult> PublishAndHydrateAsync(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        ResourceBudget budget,
        ProviderWalCatalogReadAdmission catalogReadAdmission,
        CloudMetadataWriteAdmission writeAdmission,
        DatabasePathLock cacheLock,
        Func<CancellationToken, ValueTask<ProviderCloudHydrationResult>> hydrateAsync,
        CancellationToken cancellationToken)
    {
        await lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
        var alreadyCommitted = false;
        using (var reads = new ProviderCloudControlMetadataReadSet(controlStore, budget))
        {
            var selected = await CloudMetadataAuthority.ReadCommittedFilesAsync(controlStore,
                cancellationToken, readSet: reads).ConfigureAwait(false);
            if (selected is { Count: > 0 })
            {
                alreadyCommitted = true;
            }
            if (selected is null)
            {
                throw new PantsRecoveryFailedException("The cloud metadata claim disappeared before bootstrap.");
            }
        }
        if (alreadyCommitted)
        {
            return await hydrateAsync(cancellationToken).ConfigureAwait(false);
        }

        RequireEmptyCache(localRoot, cacheLock, cancellationToken);
        await FencePristineCatalogAsync(walStore, lease, budget, catalogReadAdmission, writeAdmission, cancellationToken)
            .ConfigureAwait(false);
        await FenceEmptyDdlRegistryAsync(controlStore, lease, budget, writeAdmission, cancellationToken)
            .ConfigureAwait(false);
        await RequireNoSstObjectsAsync(sstStore, cancellationToken).ConfigureAwait(false);
        await lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);

        using var candidate = CloudControlMetadataSnapshot.CreateBootstrapCandidate(budget);
        using var authority = new CloudMetadataAuthority(controlStore, lease, budget);
        await authority.PublishAsync(candidate, cancellationToken).ConfigureAwait(false);
        await lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
        // Keep the admitted volatile candidate until its committed generation has been selected
        // and hydrated. Hydration separately owns every remote body it reads.
        return await hydrateAsync(cancellationToken).ConfigureAwait(false);
    }

    static void RequireEmptyCache(string root, DatabasePathLock cacheLock, CancellationToken cancellationToken)
    {
        StoragePathGuard.EnsureTreeHasNoLinks(root);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cacheLock.OwnsCoordinationFile(root, path))
            {
                continue;
            }
            throw new PantsRecoveryFailedException(
                $"Local cache '{path}' contains state without a committed cloud metadata generation.");
        }
    }

    static async ValueTask FencePristineCatalogAsync(ICloudObjectStore store, CloudLeaseCoordinator lease,
        ResourceBudget budget, ProviderWalCatalogReadAdmission readAdmission, CloudMetadataWriteAdmission writeAdmission,
        CancellationToken cancellationToken)
    {
        var catalogs = new ProviderWalCatalogStore(store,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            lease.EnsureValid, lease.ValidateRemoteAsync, readAdmission, writeAdmission);
        for (var attempt = 0; attempt < MaximumFenceAttempts; attempt++)
        {
            using var read = await catalogs.ReadAsync(true, cancellationToken, repairCopies: false).ConfigureAwait(false);
            var catalog = read.Catalog;
            if (catalog is not null && (catalog.Segments.Count != 0 || !catalog.Anchor.Matches(WalChainLink.Genesis) ||
                catalog.NextSegmentId != 1 || catalog.SequenceFloor != 0 || catalog.RetiredBy != 0))
            {
                throw new PantsRecoveryFailedException("Cannot bootstrap empty cloud metadata over existing WAL state.");
            }
            if (catalog?.FencingEpoch > lease.Epoch)
            {
                throw new PantsFencedException("The bootstrap WAL catalog belongs to a newer writer.");
            }

            using var bytes = writeAdmission.Serialize(new ProviderWalCatalog
            {
                FencingEpoch = lease.Epoch,
                NextSegmentId = 1
            }, JsonOptions, "Cloud bootstrap WAL catalog fence");
            await lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            if (!await store.PutAsync(PantsCloudObjectLayout.WalCatalogObjectKey, bytes.Bytes,
                    read.Version is null ? new PantsCloudObjectWriteCondition.IfAbsent() :
                        new PantsCloudObjectWriteCondition.IfVersion(read.Version), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            using var confirmation = new ProviderCloudControlMetadataReadSet(store, budget);
            var confirmed = await confirmation.ReadAsync(PantsCloudObjectLayout.WalCatalogObjectKey, cancellationToken)
                .ConfigureAwait(false);
            lease.EnsureValid();
            if (confirmed is null || !confirmed.Data.Span.SequenceEqual(bytes.Bytes.Span))
            {
                throw new PantsLeaseIndeterminateException("The bootstrap WAL catalog fence was not confirmed.");
            }
            await catalogs.ConvergeMirrorAsync(bytes.Bytes, confirmed.Version, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new PantsBusyException("Cloud bootstrap WAL fencing exceeded its bounded CAS retries.");
    }

    static async ValueTask FenceEmptyDdlRegistryAsync(ICloudObjectStore store, CloudLeaseCoordinator lease,
        ResourceBudget budget, CloudMetadataWriteAdmission writeAdmission, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumFenceAttempts; attempt++)
        {
            using var reads = new ProviderCloudControlMetadataReadSet(store, budget);
            var current = await reads.ReadAsync(PantsCloudObjectLayout.DdlRegistryObjectKey, cancellationToken)
                .ConfigureAwait(false);
            var registry = current is null ? new CloudDdlRegistry() : CloudDdlJson.DeserializeRegistry(current.Data.Span);
            if (registry.Epoch != 0 || registry.ColumnFamilies.Count != 0 || registry.Operations.Count != 0)
            {
                throw new PantsRecoveryFailedException("Cannot bootstrap empty cloud metadata over existing DDL state.");
            }
            if (registry.WriterEpoch > lease.Epoch)
            {
                throw new PantsFencedException("The bootstrap DDL registry belongs to a newer writer.");
            }

            registry.WriterEpoch = lease.Epoch;
            using var bytes = writeAdmission.Serialize(registry, CloudDdlJson.Options, "Cloud bootstrap DDL fence");
            await lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            if (!await store.PutAsync(PantsCloudObjectLayout.DdlRegistryObjectKey, bytes.Bytes,
                    current is null ? new PantsCloudObjectWriteCondition.IfAbsent() :
                        new PantsCloudObjectWriteCondition.IfVersion(current.Version), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            using var confirmation = new ProviderCloudControlMetadataReadSet(store, budget);
            var confirmed = await confirmation.ReadAsync(PantsCloudObjectLayout.DdlRegistryObjectKey, cancellationToken)
                .ConfigureAwait(false);
            lease.EnsureValid();
            if (confirmed is null || !confirmed.Data.Span.SequenceEqual(bytes.Bytes.Span))
            {
                throw new PantsLeaseIndeterminateException("The bootstrap DDL fence was not confirmed.");
            }
            return;
        }
        throw new PantsBusyException("Cloud bootstrap DDL fencing exceeded its bounded CAS retries.");
    }

    static async ValueTask RequireNoSstObjectsAsync(ICloudObjectStore store, CancellationToken cancellationToken)
    {
        string? continuation = null;
        for (var page = 0; page < MaximumInventoryPages; page++)
        {
            var listed = await store.ListPageAsync(PantsCloudObjectLayout.SstPrefix, continuation, cancellationToken)
                .ConfigureAwait(false);
            if (listed.ObjectKeys.Count != 0)
            {
                throw new PantsRecoveryFailedException("Cannot bootstrap empty cloud metadata over existing SST objects.");
            }
            if (listed.ContinuationToken is null)
            {
                return;
            }
            if (listed.ContinuationToken == continuation)
            {
                throw new PantsCorruptionException("The bootstrap SST inventory repeated its continuation token.");
            }
            continuation = listed.ContinuationToken;
        }
        throw new PantsResourceLimitException("The bootstrap SST inventory exceeded its bounded page allowance.");
    }
}
