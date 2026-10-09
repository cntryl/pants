namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed class HybridCacheManager : IDisposable
{
    readonly SemaphoreSlim _evictionGate = new(1, 1);
    readonly IFailpointHandler _failpoints;
    readonly HybridStorageBudgetPolicy _policy;
    readonly Lock _protectedSstsLock = new();
    readonly Dictionary<string, int> _protectedSsts = new(StringComparer.Ordinal);
    IHybridCacheStore? _ledgerStore;
    int _pendingEvictions;

    public HybridCacheManager(
        long maximumLocalBytes,
        IFailpointHandler? failpoints = null)
    {
        _policy = new HybridStorageBudgetPolicy(maximumLocalBytes);
        _failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        Ledger = new StorageBudgetLedger(
            _policy,
            () => Volatile.Read(ref _ledgerStore)?.LocalCommittedBytes ?? 0);
    }

    /// <summary>
    ///     Admits disk-consuming work against the local budget before it writes.
    /// </summary>
    public StorageBudgetLedger Ledger { get; }

    public int PendingEvictions => Volatile.Read(ref _pendingEvictions);

    /// <summary>
    ///     The fixed local window one compaction stages its outputs through: half the local budget,
    ///     as in Midge, leaving the other half for resident SSTs, the WAL and concurrent flushes.
    /// </summary>
    public long CompactionStagingWindowBytes => Math.Max(1, _policy.MaximumLocalBytes / 2);

    /// <summary>
    ///     Binds the store the ledger measures resident bytes from. The manager is constructed
    ///     before the store exists, so this closes that loop once during startup.
    /// </summary>
    public void BindStore(IHybridCacheStore store) => Volatile.Write(ref _ledgerStore, store);

    /// <summary>
    ///     Reserves the bytes an operation is about to write. Hold the reservation until the bytes
    ///     are published, then dispose it.
    /// </summary>
    public StorageBudgetLedger.StorageReservation Reserve(
        StorageAdmissionKind kind,
        long estimateBytes) =>
        Ledger.Reserve(kind, estimateBytes);

    /// <summary>
    ///     Reserves a flush's output, evicting to make room and proceeding regardless if that is not
    ///     enough.
    /// </summary>
    /// <remarks>
    ///     A flush is the only thing that turns a full memtable into an SST, and write admission
    ///     stalls on level-0 debt, so refusing it for space would be a permanent wedge rather than a
    ///     delay. Eviction is tried first because a hybrid database can drop local copies of SSTs
    ///     already durable in the cloud; if that still leaves too little, the flush proceeds and the
    ///     overshoot is bounded by one memtable-sized output. The bytes stay charged either way, so
    ///     concurrent callers still see the true figure and are refused correctly. Compaction does
    ///     not take this path: it stages through <see cref="ReserveCompactionStagingAsync" />.
    /// </remarks>
    public async ValueTask<StorageBudgetLedger.StorageReservation> ReserveFlushAsync(
        IHybridCacheStore store,
        long estimateBytes,
        CancellationToken cancellationToken)
    {
        if (Ledger.TryReserve(StorageAdmissionKind.Flush, estimateBytes, out var reservation))
        {
            return reservation;
        }

        await EvictIfNeededAsync(store, cancellationToken).ConfigureAwait(false);
        return Ledger.TryReserve(StorageAdmissionKind.Flush, estimateBytes, out reservation)
            ? reservation
            : Ledger.ReserveUnconditionally(estimateBytes);
    }

    /// <summary>
    ///     Reserves a compaction's fixed local staging window, evicting cloud-durable local SSTs
    ///     until it fits, and refusing with <see cref="PantsNoSpaceException" /> when it cannot.
    /// </summary>
    /// <remarks>
    ///     Compaction reads cold inputs through ranged remote reads and drains each output before
    ///     staging the next, so this window is its whole local footprint. Unlike a flush it is never
    ///     admitted unconditionally: a refusal leaves every input authoritative and is retried, while
    ///     an unconditional admission would let one compaction overrun the operator's cap.
    /// </remarks>
    public async ValueTask<StorageBudgetLedger.StorageReservation> ReserveCompactionStagingAsync(
        IHybridCacheStore store,
        long windowBytes,
        CancellationToken cancellationToken)
    {
        if (Ledger.TryReserve(StorageAdmissionKind.Compaction, windowBytes, out var reservation))
        {
            return reservation;
        }

        await EvictToFitAsync(store, windowBytes, cancellationToken).ConfigureAwait(false);
        return Ledger.Reserve(StorageAdmissionKind.Compaction, windowBytes);
    }

    /// <summary>
    ///     Prevents maintenance inputs from being evicted until the returned lease is disposed.
    /// </summary>
    public async ValueTask<IDisposable> ProtectSstsFromEvictionAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var protectedNames = names.Distinct(StringComparer.Ordinal).ToArray();
        await _evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_protectedSstsLock)
            {
                foreach (var name in protectedNames)
                {
                    _protectedSsts[name] = _protectedSsts.GetValueOrDefault(name) + 1;
                }
            }
        }
        finally
        {
            _evictionGate.Release();
        }

        return new EvictionProtection(this, protectedNames);
    }

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
                store.GetLocalManifestSsts()
                    .Where(candidate => !IsProtected(candidate.Name))
                    .ToArray());
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

    /// <summary>
    ///     Evicts unprotected, cloud-verified local SSTs, oldest first, until
    ///     <paramref name="requiredBytes" /> more fit under the local budget.
    /// </summary>
    async ValueTask EvictToFitAsync(
        IHybridCacheStore store,
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        await _evictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = store.GetLocalManifestSsts()
                .Where(candidate => !IsProtected(candidate.Name))
                .ToArray();
            var projectedBytes = Ledger.ChargedBytes;
            var planned = new List<HybridLocalSst>();
            foreach (var candidate in candidates)
            {
                if (checked(projectedBytes + requiredBytes) <= _policy.MaximumLocalBytes)
                {
                    break;
                }

                planned.Add(candidate);
                projectedBytes = Math.Max(0, projectedBytes - candidate.SizeBytes);
            }

            if (checked(projectedBytes + requiredBytes) > _policy.MaximumLocalBytes)
            {
                // Evicting cannot make the window fit, so keep the local cache warm.
                return;
            }

            Volatile.Write(ref _pendingEvictions, planned.Count);
            _failpoints.Hit(Failpoint.BeforeHybridSstEviction);
            foreach (var candidate in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (store.IsSstLocal(candidate.Name))
                {
                    await store.VerifyRemoteSstMatchesLocalAsync(candidate.Name, cancellationToken)
                        .ConfigureAwait(false);
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

    /// <summary>
    ///     Downloads any of <paramref name="names" /> that are not resident, reserving each file's
    ///     recorded size before fetching it.
    /// </summary>
    /// <remarks>
    ///     Hydration is the one path that grows local disk without the caller asking for a write, so
    ///     leaving it unbudgeted let a read of enough cold SSTs push past the operator's cap.
    /// </remarks>
    public async ValueTask EnsureLocalSstsAsync(
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

            var estimate = store.TryGetManifestSstSizeBytes(name, out var sizeBytes)
                ? sizeBytes
                : 0;
            using var reservation = Reserve(StorageAdmissionKind.Hydration, estimate);
            await store.HydrateLocalSstAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    bool IsProtected(string name)
    {
        lock (_protectedSstsLock)
        {
            return _protectedSsts.ContainsKey(name);
        }
    }

    void ReleaseProtection(IReadOnlyList<string> names)
    {
        lock (_protectedSstsLock)
        {
            foreach (var name in names)
            {
                var count = _protectedSsts[name];
                if (count == 1)
                {
                    _protectedSsts.Remove(name);
                }
                else
                {
                    _protectedSsts[name] = count - 1;
                }
            }
        }
    }

    sealed class EvictionProtection(
        HybridCacheManager owner,
        IReadOnlyList<string> names) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseProtection(names);
            }
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
