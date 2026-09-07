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
    long _reservedBytes;

    public StorageBudgetLedger(HybridStorageBudgetPolicy policy, Func<long> residentBytes)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _residentBytes = residentBytes ?? throw new ArgumentNullException(nameof(residentBytes));
    }

    public long ReservedBytes => Volatile.Read(ref _reservedBytes);

    /// <summary>Resident bytes plus everything promised but not yet written.</summary>
    public long ChargedBytes => checked(_residentBytes() + ReservedBytes);

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
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _reservedBytes,
                    reserved + estimateBytes,
                    reserved) == reserved)
            {
                reservation = new StorageReservation(this, estimateBytes);
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
    public StorageReservation ReserveUnconditionally(long estimateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateBytes);
        Interlocked.Add(ref _reservedBytes, estimateBytes);
        return new StorageReservation(this, estimateBytes);
    }

    /// <summary>
    ///     Flush and compaction are what turn memtables and overlapping files back into free space,
    ///     so refusing them at the watermark that refuses user writes would deadlock a full
    ///     database. They admit up to the real limit; everything else stops at the emergency
    ///     watermark, leaving that margin for them.
    /// </summary>
    long GetCeilingBytes(StorageAdmissionKind kind) => kind switch
    {
        StorageAdmissionKind.Flush or StorageAdmissionKind.Compaction =>
            _policy.MaximumLocalBytes,
        _ => _policy.MaximumLocalBytes *
            HybridStorageBudgetPolicy.EmergencyWatermarkPercent / 100
    };

    void Release(long bytes)
    {
        if (bytes != 0)
        {
            Interlocked.Add(ref _reservedBytes, -bytes);
        }
    }

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
        readonly long _estimateBytes;
        readonly StorageBudgetLedger _ledger;
        int _released;

        internal StorageReservation(StorageBudgetLedger ledger, long estimateBytes)
        {
            _ledger = ledger;
            _estimateBytes = estimateBytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ledger.Release(_estimateBytes);
            }
        }
    }
}
