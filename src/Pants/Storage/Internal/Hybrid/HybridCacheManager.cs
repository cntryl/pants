namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed class HybridCacheManager : IDisposable
{
    readonly SemaphoreSlim _evictionGate = new(1, 1);
    readonly IFailpointHandler _failpoints;
    readonly HybridStorageBudgetPolicy _policy;
    readonly Lock _snapshotGate = new();
    readonly Dictionary<long, long> _snapshotPins = [];
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

    public bool RequiresEviction(LocalDiskStore store) =>
        _policy.GetWatermark(store.LocalCommittedBytes) != HybridStorageWatermark.Normal;

    public void RegisterSnapshot(long snapshotId, long sequence)
    {
        lock (_snapshotGate)
        {
            if (!_snapshotPins.TryAdd(snapshotId, sequence))
            {
                throw new PantsInternalException(
                    $"Hybrid snapshot identifier {snapshotId} is already registered.");
            }
        }
    }

    public void UnregisterSnapshot(long snapshotId)
    {
        lock (_snapshotGate)
        {
            _snapshotPins.Remove(snapshotId);
        }
    }

    public void EnsureWriteAdmitted(LocalDiskStore store, RuntimeState state)
    {
        if (_policy.GetWatermark(store.LocalCommittedBytes) != HybridStorageWatermark.Emergency)
        {
            return;
        }

        throw new PantsNoSpaceException(
            "The hybrid local cache is at its emergency watermark and cannot admit writes.");
    }

    public async ValueTask EvictIfNeededAsync(
        LocalDiskStore store,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> fetchCloudSst,
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
                store.GetLocalManifestSsts(),
                GetSnapshotSequences());
            Volatile.Write(ref _pendingEvictions, planned.Count);
            _failpoints.Hit(Failpoint.BeforeHybridSstEviction);
            foreach (var candidate in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localBytes = store.ReadLocalSstForEviction(candidate.Name);
                if (localBytes is null)
                {
                    Interlocked.Decrement(ref _pendingEvictions);
                    continue;
                }

                var cloudBytes = await fetchCloudSst(candidate.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (cloudBytes is null || !cloudBytes.Value.Span.SequenceEqual(localBytes.Value.Span))
                {
                    throw new PantsRecoveryFailedException(
                        $"Hybrid SST '{candidate.Name}' is not confirmed durable in cloud storage.");
                }

                lock (_snapshotGate)
                {
                    if (IsPinned(candidate, _snapshotPins.Values))
                    {
                        Interlocked.Decrement(ref _pendingEvictions);
                        continue;
                    }

                    store.EvictLocalSst(candidate.Name);
                }

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
        IReadOnlyList<HybridLocalSst> candidates,
        IReadOnlyCollection<long> activeSnapshotSequences)
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

            if (IsPinned(candidate, activeSnapshotSequences))
            {
                continue;
            }

            planned.Add(candidate);
            projectedBytes = Math.Max(0, projectedBytes - candidate.SizeBytes);
        }

        return planned;
    }

    static bool IsPinned(
        HybridLocalSst candidate,
        IReadOnlyCollection<long> activeSnapshotSequences)
    {
        if (activeSnapshotSequences.Count == 0)
        {
            return false;
        }

        if (!candidate.SmallestSequence.HasValue)
        {
            return true;
        }

        return activeSnapshotSequences.Any(sequence =>
            sequence >= 0 && checked((ulong)sequence) >= candidate.SmallestSequence.Value);
    }

    long[] GetSnapshotSequences()
    {
        lock (_snapshotGate)
        {
            return _snapshotPins.Values.ToArray();
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
            PendingEvictions);
    }
}
