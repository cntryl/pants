namespace Cntryl.Pants.Storage.Internal.Recovery;

/// <summary>
///     Bounds the working set of WAL replay: decides when the recovered memtables must be published
///     as a checkpoint, refuses a transaction that cannot be recovered within the working set, and
///     charges each checkpoint's staging to the local-storage ledger while it is written.
/// </summary>
/// <remarks>
///     The target is derived from both budgets. The memtable size limit (itself carved from the
///     memory budget) bounds recovered memory; where a local-storage budget applies, half its free
///     space at replay start determines when to checkpoint. A transaction is applied whole, so it
///     cannot be split across checkpoints: beyond a generous multiple of the memory target it is a
///     resource failure. A hybrid cache may allow a checkpoint to exceed its current soft local
///     budget, so that budget must not reject the transaction itself. In strict capacity mode, a
///     transaction beyond disk capacity is a no-space failure. Neither resource failure is
///     corruption, so salvage never truncates the WAL to get past it.
/// </remarks>
sealed class RecoveryCheckpointer
{
    const long TransactionMemoryLimitMultiple = 8;
    const long RecoveredEntryOverheadBytes = 64;

    readonly long? _diskCapacityBytes;
    readonly bool _allowCheckpointCapacityOvershoot;
    readonly StorageBudgetLedger? _ledger;
    readonly long _transactionMemoryLimitBytes;

    RecoveryCheckpointer(
        long targetBytes,
        long transactionMemoryLimitBytes,
        long? diskCapacityBytes,
        bool allowCheckpointCapacityOvershoot,
        StorageBudgetLedger? ledger)
    {
        TargetBytes = targetBytes;
        _transactionMemoryLimitBytes = transactionMemoryLimitBytes;
        _diskCapacityBytes = diskCapacityBytes;
        _allowCheckpointCapacityOvershoot = allowCheckpointCapacityOvershoot;
        _ledger = ledger;
    }

    /// <summary>Recovered bytes at which replay publishes a checkpoint.</summary>
    public long TargetBytes { get; }

    /// <summary>
    ///     Derives the working set from <paramref name="memtableTargetBytes" /> and, when
    ///     <paramref name="ledger" /> is present, the local storage it leaves free right now.
    /// </summary>
    /// <param name="memtableTargetBytes">The configured recovered-memtable target.</param>
    /// <param name="ledger">The optional local-storage budget ledger.</param>
    /// <param name="allowCheckpointCapacityOvershoot">
    ///     Whether hybrid recovery may checkpoint an indivisible transaction without rejecting it
    ///     solely because of transient cache headroom. Checkpoint output remains charged against
    ///     the SST upper bound, and over-budget staging is permitted only while current charges are
    ///     within the cache limit.
    /// </param>
    public static RecoveryCheckpointer Create(
        long memtableTargetBytes,
        StorageBudgetLedger? ledger,
        bool allowCheckpointCapacityOvershoot = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memtableTargetBytes);
        var memoryLimit = memtableTargetBytes > long.MaxValue / TransactionMemoryLimitMultiple
            ? long.MaxValue
            : memtableTargetBytes * TransactionMemoryLimitMultiple;
        long? diskCapacity = ledger is null
            ? null
            : ledger.GetAvailableBytes(StorageAdmissionKind.Flush) / 2;
        var target = diskCapacity is { } capacity
            ? Math.Min(memtableTargetBytes, capacity)
            : memtableTargetBytes;
        return new RecoveryCheckpointer(
            target,
            memoryLimit,
            diskCapacity,
            allowCheckpointCapacityOvershoot,
            ledger);
    }

    public bool IsDue(long recoveredBytes) => recoveredBytes > 0 && recoveredBytes >= TargetBytes;

    /// <summary>
    ///     Throws a typed resource error when <paramref name="mutations" />, one indivisible
    ///     transaction, cannot be recovered within the working set.
    /// </summary>
    public void RequireTransactionFits(IEnumerable<WalMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        RequireTransactionFits(visitor =>
        {
            foreach (var mutation in mutations)
            {
                visitor(mutation);
            }
        });
    }

    /// <summary>
    ///     Checks mutations as they are visited, so callers with spilled operations do not need to
    ///     materialize a complete transaction to apply the same recovery bound before WAL append.
    /// </summary>
    public void RequireTransactionFits(Action<Action<WalMutation>> enumerate)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        long bytes = 0;
        enumerate(mutation =>
        {
            bytes = checked(bytes + EstimateBytes(mutation));
            if (bytes > _transactionMemoryLimitBytes)
            {
                throw PantsException.ResourceLimit(
                    $"A WAL transaction needs more than {_transactionMemoryLimitBytes} bytes to " +
                    "recover, above the recovery working set; the WAL was left untouched.");
            }
        });

        if (!_allowCheckpointCapacityOvershoot &&
            bytes > 0 &&
            _diskCapacityBytes is { } capacity &&
            bytes > capacity)
        {
            throw new PantsNoSpaceException(
                $"A WAL transaction needs {bytes} bytes to recover, but local storage leaves only " +
                $"{capacity} bytes of recovery checkpoint capacity; the WAL was left untouched.");
        }
    }

    /// <summary>
    ///     Runs <paramref name="publish" /> while <paramref name="stagingBytes" /> of checkpoint
    ///     staging is charged to the local-storage ledger. Callers pass a conservative encoded SST
    ///     upper bound, not the recovered memtable's logical size.
    /// </summary>
    /// <remarks>
    ///     A checkpoint releases recovery memory, so hybrid recovery may exceed the soft cache
    ///     budget to make progress. That allowance applies to one checkpoint at a time: if retained
    ///     files already put the cache above budget, another over-budget checkpoint is rejected.
    ///     The staging bytes remain charged even when admission is bypassed.
    /// </remarks>
    public void Publish(long stagingBytes, Action publish)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stagingBytes);
        ArgumentNullException.ThrowIfNull(publish);
        using var reservation = ReserveStaging(stagingBytes);
        publish();
    }

    StorageBudgetLedger.StorageReservation? ReserveStaging(long bytes)
    {
        if (_ledger is null)
        {
            return null;
        }

        if (_ledger.TryReserve(StorageAdmissionKind.Flush, bytes, out var reservation))
        {
            return reservation;
        }

        if (!_allowCheckpointCapacityOvershoot)
        {
            return _ledger.Reserve(StorageAdmissionKind.Flush, bytes);
        }

        if (_ledger.ChargedBytes > _ledger.GetAdmissionCeilingBytes(StorageAdmissionKind.Flush))
        {
            throw new PantsNoSpaceException(
                "A recovery checkpoint cannot exceed the local cache budget while retained " +
                "recovery files are already over budget.");
        }

        return _ledger.ReserveUnconditionally(bytes);
    }

    static long EstimateBytes(WalMutation mutation) =>
        (long)mutation.Key.Length +
        (mutation.Value?.Length ?? 0) +
        (mutation.RangeEnd?.Length ?? 0) +
        RecoveredEntryOverheadBytes;
}
