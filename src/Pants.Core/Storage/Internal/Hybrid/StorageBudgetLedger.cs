using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Pants.Storage.Internal.Hybrid;

/// <summary>
///     Admits local-disk consumers against the configured budget by reserving the bytes an
///     operation is about to write, before it writes them.
/// </summary>
/// <remarks>
///     Comparing a watermark against bytes already on disk is not sufficient: a flush, a compaction
///     and a hydration can each read the same free figure and each decide it fits, then all three
///     land and together overrun the operator's cap. Counting outstanding reservations alongside
///     resident bytes closes that window, at the cost of admitting on an estimate — so callers
///     settle to the measured size once it is known.
/// </remarks>
sealed class StorageBudgetLedger
{
    readonly HybridStorageBudgetPolicy _policy;
    readonly Func<long> _residentBytes;
    readonly long[] _reservedBytesByKind = new long[Enum.GetValues<StorageAdmissionKind>().Length];
    readonly int[] _reservationCountsByKind = new int[Enum.GetValues<StorageAdmissionKind>().Length];
    readonly Lock _admissionGate = new();
    readonly RejectedAdmission?[] _rejectedAdmissions =
        new RejectedAdmission?[Enum.GetValues<StorageAdmissionKind>().Length];
    long _reservedBytes;
    long _admissionRejectionsTotal;

    public StorageBudgetLedger(HybridStorageBudgetPolicy policy, Func<long> residentBytes)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _residentBytes = residentBytes ?? throw new ArgumentNullException(nameof(residentBytes));
    }

    public long ReservedBytes => Volatile.Read(ref _reservedBytes);

    public long AdmissionRejectionsTotal => Interlocked.Read(ref _admissionRejectionsTotal);

    public PantsStorageAdmissionBlock? BlockedAdmission
    {
        get
        {
            lock (_admissionGate)
            {
                RejectedAdmission? oldest = null;
                foreach (var rejection in _rejectedAdmissions)
                {
                    if (rejection is not null &&
                        (oldest is null || rejection.FirstObservedTimestamp < oldest.FirstObservedTimestamp))
                    {
                        oldest = rejection;
                    }
                }

                if (oldest is null)
                {
                    return null;
                }

                return new PantsStorageAdmissionBlock
                {
                    Operation = ToPublicKind(oldest.Kind),
                    Reason = oldest.Reason,
                    RequestedBytes = oldest.RequestedBytes,
                    FreeBytesAtRejection = oldest.FreeBytesAtRejection,
                    AgeMilliseconds = Math.Max(
                        0,
                        (long)Stopwatch.GetElapsedTime(oldest.FirstObservedTimestamp).TotalMilliseconds),
                    Attempts = oldest.Attempts
                };
            }
        }
    }

    /// <summary>Resident bytes plus everything promised but not yet written.</summary>
    public long ChargedBytes => checked(_residentBytes() + ReservedBytes);

    public long GetReservedBytes(StorageAdmissionKind kind) =>
        Volatile.Read(ref _reservedBytesByKind[(int)kind]);

    public int GetReservationCount(StorageAdmissionKind kind) =>
        Volatile.Read(ref _reservationCountsByKind[(int)kind]);

    /// <summary>Records a local-capacity rejection made by a caller-side preflight check.</summary>
    public void RecordCapacityRejection(StorageAdmissionKind kind, long estimateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateBytes);
        var charged = ChargedBytes;
        ObserveRejection(
            kind,
            estimateBytes,
            _policy.GetFreeBytes(charged),
            PantsStorageAdmissionReason.LocalCapacity);
    }

    public void RecordAdmissionSuccess(StorageAdmissionKind kind) => ClearRejection(kind);

    /// <summary>
    ///     Bytes a <paramref name="kind" /> operation could still reserve right now.
    /// </summary>
    public long GetAvailableBytes(StorageAdmissionKind kind) =>
        Math.Max(0, GetAdmissionCeilingBytes(kind) - ChargedBytes);

    /// <summary>
    ///     The hard charged-byte limit for <paramref name="kind" />, even when current charges
    ///     already exceed it.
    /// </summary>
    public long GetAdmissionCeilingBytes(StorageAdmissionKind kind) => GetCeilingBytes(kind);

    /// <summary>
    ///     Reserves <paramref name="estimateBytes" /> or throws.
    /// </summary>
    public StorageReservation Reserve(StorageAdmissionKind kind, long estimateBytes)
    {
        if (TryReserve(kind, estimateBytes, out var reservation))
        {
            return reservation;
        }

        throw new PantsNoSpaceException(
            $"The hybrid local cache cannot admit {estimateBytes} bytes for {kind}: " +
            $"{ChargedBytes} of {_policy.MaximumLocalBytes} bytes are already charged.");
    }

    /// <summary>
    ///     Reserves <paramref name="estimateBytes" /> if it fits, reporting failure instead of
    ///     throwing.
    /// </summary>
    public bool TryReserve(
        StorageAdmissionKind kind,
        long estimateBytes,
        [NotNullWhen(true)] out StorageReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateBytes);

        var ceiling = GetCeilingBytes(kind);
        while (true)
        {
            var reserved = Volatile.Read(ref _reservedBytes);
            var charged = checked(_residentBytes() + reserved);
            if (checked(charged + estimateBytes) > ceiling)
            {
                reservation = null;
                ObserveRejection(
                    kind,
                    estimateBytes,
                    _policy.GetFreeBytes(charged),
                    PantsStorageAdmissionReason.LocalCapacity);
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _reservedBytes,
                    reserved + estimateBytes,
                    reserved) == reserved)
            {
                Interlocked.Add(ref _reservedBytesByKind[(int)kind], estimateBytes);
                Interlocked.Increment(ref _reservationCountsByKind[(int)kind]);
                ClearRejection(kind);
                reservation = new StorageReservation(this, kind, estimateBytes);
                return true;
            }
        }
    }

    /// <summary>
    ///     Charges <paramref name="estimateBytes" /> without applying admission control.
    /// </summary>
    /// <remarks>
    ///     Reserved for work that must not be blocked. The bytes are still charged, so concurrent
    ///     operations see the true figure and are refused correctly — this bypasses the decision,
    ///     never the accounting.
    /// </remarks>
    public StorageReservation ReserveUnconditionally(StorageAdmissionKind kind, long estimateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateBytes);
        Interlocked.Add(ref _reservedBytes, estimateBytes);
        Interlocked.Add(ref _reservedBytesByKind[(int)kind], estimateBytes);
        Interlocked.Increment(ref _reservationCountsByKind[(int)kind]);
        ClearRejection(kind);
        return new StorageReservation(this, kind, estimateBytes);
    }

    /// <summary>
    ///     Flush and compaction are what turn memtables and overlapping files back into free space,
    ///     so refusing them at the watermark that refuses user writes would deadlock a full
    ///     database. They admit up to the real limit; everything else stops at the emergency
    ///     watermark, leaving that margin for them.
    /// </summary>
    long GetCeilingBytes(StorageAdmissionKind kind) => kind switch
    {
        StorageAdmissionKind.FlushHeadroom or
        StorageAdmissionKind.Flush or
        StorageAdmissionKind.Compaction =>
            _policy.MaximumLocalBytes,
        _ => _policy.MaximumLocalBytes *
            HybridStorageBudgetPolicy.EmergencyWatermarkPercent / 100
    };

    void Release(StorageAdmissionKind kind, long bytes)
    {
        Interlocked.Add(ref _reservedBytes, -bytes);
        Interlocked.Add(ref _reservedBytesByKind[(int)kind], -bytes);
        Interlocked.Decrement(ref _reservationCountsByKind[(int)kind]);
    }

    void ObserveRejection(
        StorageAdmissionKind kind,
        long requestedBytes,
        long freeBytes,
        PantsStorageAdmissionReason reason)
    {
        lock (_admissionGate)
        {
            var previous = _rejectedAdmissions[(int)kind];
            _rejectedAdmissions[(int)kind] = new RejectedAdmission(
                kind,
                reason,
                requestedBytes,
                freeBytes,
                previous?.FirstObservedTimestamp ?? Stopwatch.GetTimestamp(),
                previous is null || previous.Attempts == long.MaxValue
                    ? previous?.Attempts ?? 1
                    : previous.Attempts + 1);
            IncrementSaturated(ref _admissionRejectionsTotal);
        }
    }

    static void IncrementSaturated(ref long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current == long.MaxValue ||
                Interlocked.CompareExchange(ref value, current + 1, current) == current)
            {
                return;
            }
        }
    }

    void ClearRejection(StorageAdmissionKind kind)
    {
        lock (_admissionGate)
        {
            _rejectedAdmissions[(int)kind] = null;
        }
    }

    static PantsStorageAdmissionKind ToPublicKind(StorageAdmissionKind kind) => kind switch
    {
        StorageAdmissionKind.Wal => PantsStorageAdmissionKind.Wal,
        StorageAdmissionKind.TransactionSpill => PantsStorageAdmissionKind.TransactionSpill,
        StorageAdmissionKind.RecoverySpool => PantsStorageAdmissionKind.RecoverySpool,
        StorageAdmissionKind.Hydration => PantsStorageAdmissionKind.Hydration,
        StorageAdmissionKind.FlushHeadroom => PantsStorageAdmissionKind.FlushHeadroom,
        StorageAdmissionKind.Flush => PantsStorageAdmissionKind.Flush,
        StorageAdmissionKind.Compaction => PantsStorageAdmissionKind.Compaction,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown storage admission kind.")
    };

    sealed record RejectedAdmission(
        StorageAdmissionKind Kind,
        PantsStorageAdmissionReason Reason,
        long RequestedBytes,
        long FreeBytesAtRejection,
        long FirstObservedTimestamp,
        long Attempts);

    /// <summary>
    ///     An outstanding claim on the budget, held from before the bytes are written until after
    ///     they are published.
    /// </summary>
    /// <remarks>
    ///     There is no separate settle step because resident bytes are measured from the store: once
    ///     the operation publishes, its bytes are counted there, so releasing the estimate is all
    ///     that remains. Hold the reservation until publication completes — while both the estimate
    ///     and the published bytes are charged the ledger over-counts, which is the safe direction;
    ///     releasing early would reopen the overrun window this type exists to close.
    /// </remarks>
    internal sealed class StorageReservation : IDisposable
    {
        readonly StorageAdmissionKind _kind;
        readonly long _estimateBytes;
        readonly StorageBudgetLedger _ledger;
        int _released;

        internal StorageReservation(
            StorageBudgetLedger ledger,
            StorageAdmissionKind kind,
            long estimateBytes)
        {
            _ledger = ledger;
            _kind = kind;
            _estimateBytes = estimateBytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ledger.Release(_kind, _estimateBytes);
            }
        }
    }
}
