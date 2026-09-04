namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed class HybridCacheManager : IDisposable
{
    readonly SemaphoreSlim _evictionGate = new(1, 1);
    readonly IFailpointHandler _failpoints;
    readonly HybridStorageBudgetPolicy _policy;
    int _pendingEvictions;

    public HybridCacheManager(
        long maximumLocalBytes,
        IFailpointHandler? failpoints = null)
    {
        _policy = new HybridStorageBudgetPolicy(maximumLocalBytes);
        _failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
    }

    public int PendingEvictions => Volatile.Read(ref _pendingEvictions);

    public void Dispose() => _evictionGate.Dispose();

    public bool RequiresEviction(IHybridCacheStore store) =>
        _policy.GetWatermark(store.LocalCommittedBytes) != HybridStorageWatermark.Normal;

    public void EnsureWriteAdmitted(IHybridCacheStore store, RuntimeState state)
    {
        if (_policy.GetWatermark(store.LocalCommittedBytes) != HybridStorageWatermark.Emergency)
        {
            return;
        }

        throw new PantsNoSpaceException(
            "The hybrid local cache is at its emergency watermark and cannot admit writes.");
    }

    public async ValueTask EvictIfNeededAsync(
        IHybridCacheStore store,
        CancellationToken cancellationToken)
    {
        await _evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RequiresEviction(store))
            {
                return;
            }

            var planned = PlanEvictions(
                store.LocalCommittedBytes,
                store.GetLocalManifestSsts());
            Volatile.Write(ref _pendingEvictions, planned.Count);
            _failpoints.Hit(Failpoint.BeforeHybridSstEviction);
            foreach (var candidate in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!store.IsSstLocal(candidate.Name))
                {
                    Interlocked.Decrement(ref _pendingEvictions);
                    continue;
                }

                await store.VerifyRemoteSstMatchesLocalAsync(candidate.Name, cancellationToken)
                    .ConfigureAwait(false);

                // Snapshot visibility is owned by the immutable remote object named in the
                // snapshot's manifest view. A local source already opened for a read holds a
                // delete-share file handle; a later read falls back to the remote source.
                store.EvictLocalSst(candidate.Name);

                Interlocked.Decrement(ref _pendingEvictions);
            }
        }
        finally
        {
            Volatile.Write(ref _pendingEvictions, 0);
            _evictionGate.Release();
        }
    }

    List<HybridLocalSst> PlanEvictions(
        long totalCommittedBytes,
        IReadOnlyList<HybridLocalSst> candidates)
    {
        var planned = new List<HybridLocalSst>();
        var projectedBytes = totalCommittedBytes;
        foreach (var candidate in candidates)
        {
            if (_policy.GetUsagePercent(projectedBytes) <
                HybridStorageBudgetPolicy.HighWatermarkPercent)
            {
                break;
            }

            planned.Add(candidate);
            projectedBytes = Math.Max(0, projectedBytes - candidate.SizeBytes);
        }

        return planned;
    }

    public static async ValueTask EnsureLocalSstsAsync(
        IHybridCacheStore store,
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.IsSstLocal(name))
            {
                continue;
            }

            await store.HydrateLocalSstAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    public HybridCacheMetrics GetMetrics(IHybridCacheStore store)
    {
        var total = store.LocalCommittedBytes;
        return new HybridCacheMetrics(
            _policy.MaximumLocalBytes,
            total,
            _policy.GetFreeBytes(total),
            _policy.GetUsagePercent(total),
            PendingEvictions);
    }
}
