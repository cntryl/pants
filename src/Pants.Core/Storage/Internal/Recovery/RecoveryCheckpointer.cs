namespace Cntryl.Pants.Storage.Internal.Recovery;

/// <summary>
///     Bounds the working set of WAL replay: decides when the recovered memtables must be published
///     as a checkpoint, refuses a transaction that cannot be recovered within the working set, and
///     charges each checkpoint's staging to the local-storage ledger while it is written.
/// </summary>
/// <remarks>
///     The target is derived from both budgets. The memtable size limit (itself carved from the
///     memory budget) bounds recovered memory; where a local-storage budget applies, half of its
///     free space at replay start bounds each checkpoint, so a checkpoint never needs more disk than
///     the database has left. A transaction is applied whole, so it cannot be split across
///     checkpoints: beyond a generous multiple of the memory target it is a resource failure.
///     A hybrid cache may allow checkpoint publication to exceed its current local budget, so that
///     soft budget must not reject the transaction itself. Neither resource limit is corruption,
///     so salvage never truncates the WAL to get past it.
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
    ///     Whether a hybrid cache checkpoint may proceed beyond its current soft local budget.
    ///     The ledger still determines the checkpoint target and accounts for staging; this option
    ///     only avoids rejecting a transaction based on transient cache headroom.
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
    ///     Runs <paramref name="publish" /> while <paramref name="recoveredBytes" /> of checkpoint
    ///     staging is charged to the local-storage ledger.
    /// </summary>
    /// <remarks>
    ///     A checkpoint is what releases recovery memory, so like a flush it is admitted up to the
    ///     real limit and proceeds charged even past it rather than wedging startup: the decision is
    ///     bypassed, never the accounting.
    /// </remarks>
    public void Publish(long recoveredBytes, Action publish)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recoveredBytes);
        ArgumentNullException.ThrowIfNull(publish);
        using var reservation = ReserveStaging(recoveredBytes);
        publish();
    }

    StorageBudgetLedger.StorageReservation? ReserveStaging(long bytes)
    {
        if (_ledger is null)
        {
            return null;
        }

        return _ledger.TryReserve(StorageAdmissionKind.Flush, bytes, out var reservation)
            ? reservation
            : _ledger.ReserveUnconditionally(bytes);
    }

    static long EstimateBytes(WalMutation mutation) =>
        (long)mutation.Key.Length +
        (mutation.Value?.Length ?? 0) +
        (mutation.RangeEnd?.Length ?? 0) +
        RecoveredEntryOverheadBytes;
}
