namespace Pants;

sealed class HybridCacheManager
{
    readonly HybridStorageBudgetPolicy _policy;
    int _pendingEvictions;

    public HybridCacheManager(long maximumLocalBytes)
    {
        _policy = new HybridStorageBudgetPolicy(maximumLocalBytes);
    }

    public void EnsureWriteAdmitted(LocalDiskStore store, PantsRuntimeState state)
    {
        if (_policy.GetWatermark(store.LocalCommittedBytes) != HybridStorageWatermark.Emergency)
        {
            return;
        }

        state.NoSpaceEvents = checked(state.NoSpaceEvents + 1);
        throw new PantsNoSpaceException(
            "The hybrid local cache is at its emergency watermark and cannot admit writes.");
    }

    public void EvictIfNeeded(LocalDiskStore store, bool hasActiveSnapshots)
    {
        if (hasActiveSnapshots ||
            _policy.GetWatermark(store.LocalCommittedBytes) == HybridStorageWatermark.Normal)
        {
            return;
        }

        var candidates = store.GetLocalManifestSsts();
        Volatile.Write(ref _pendingEvictions, candidates.Count);
        try
        {
            foreach (var candidate in candidates)
            {
                if (_policy.GetUsagePercent(store.LocalCommittedBytes) <
                    HybridStorageBudgetPolicy.HighWatermarkPercent)
                {
                    break;
                }

                store.EvictLocalSst(candidate.Name);
                Interlocked.Decrement(ref _pendingEvictions);
            }
        }
        finally
        {
            Volatile.Write(ref _pendingEvictions, 0);
        }
    }

    public static async ValueTask EnsureLocalSstsAsync(
        LocalDiskStore store,
        IEnumerable<string> names,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> fetch,
        CancellationToken cancellationToken)
    {
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.IsSstLocal(name))
            {
                continue;
            }

            var bytes = await fetch(name, cancellationToken).ConfigureAwait(false) ??
                throw new PantsRecoveryFailedException(
                    $"Manifest-owned cloud SST '{name}' is missing during cache hydration.");
            store.HydrateLocalSst(name, bytes.Span);
        }
    }

    public HybridCacheMetrics GetMetrics(LocalDiskStore store)
    {
        var total = store.LocalCommittedBytes;
        return new HybridCacheMetrics(
            _policy.MaximumLocalBytes,
            total,
            _policy.GetFreeBytes(total),
            _policy.GetUsagePercent(total),
            Volatile.Read(ref _pendingEvictions));
    }
}
