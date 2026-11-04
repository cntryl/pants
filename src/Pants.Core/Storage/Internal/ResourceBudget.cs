using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Shared bounded-resource accounting for internal streaming pipelines (compaction's merge
///     buffers, a scan's k-way merge buffers) — mirrors Midge's <c>ResourceBudget</c>/
///     <c>ResourceReservation</c>. <see cref="Reserve" /> is RAII-style: it throws immediately if the
///     reservation would exceed <see cref="Limit" />, and the returned <see cref="IDisposable" />
///     releases it. This bounds a single streaming operation's transient memory independent of how
///     much data it is processing in total, rather than merely observing it after the fact.
/// </summary>
sealed class ResourceBudget(long limit)
{
    long _current;
    long _peak;
    Func<bool>? _reclaim;

    public long Limit { get; } = limit;

    public long Current => Interlocked.Read(ref _current);

    public long Peak => Interlocked.Read(ref _peak);

    public void SetReclaimer(Func<bool>? reclaim) => _reclaim = reclaim;

    public IDisposable Reserve(long bytes)
    {
        while (true)
        {
            if (TryReserve(bytes, out var reservation, out var before))
            {
                return reservation;
            }

            if (bytes > Limit || _reclaim?.Invoke() != true)
            {
                throw PantsException.ResourceLimit(
                    $"Reserving {bytes} bytes would exceed the {Limit}-byte resource budget " +
                    $"({before} bytes already reserved).");
            }
        }
    }

    public Reservation ReserveOwned(long bytes) => (Reservation)Reserve(bytes);

    /// <summary>Reserves <paramref name="bytes" /> only when they fit right now.</summary>
    public bool TryReserve(long bytes, [NotNullWhen(true)] out IDisposable? reservation) =>
        TryReserve(bytes, out reservation, out _);

    bool TryReserve(
        long bytes,
        [NotNullWhen(true)] out IDisposable? reservation,
        out long before)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            before = Interlocked.Read(ref _current);
            if (bytes > Limit - before)
            {
                reservation = null;
                return false;
            }

            var after = before + bytes;
            if (Interlocked.CompareExchange(ref _current, after, before) == before)
            {
                UpdatePeak(after);
                reservation = new Reservation(this, bytes);
                return true;
            }
        }
    }

    void Release(long bytes) => Interlocked.Add(ref _current, -bytes);

    void UpdatePeak(long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _peak);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref _peak, candidate, current) == current)
            {
                return;
            }
        }
    }

    public sealed class Reservation(ResourceBudget budget, long bytes) : IDisposable
    {
        long _bytes = bytes;
        int _released;

        public void ShrinkTo(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            while (true)
            {
                var before = Volatile.Read(ref _bytes);
                if (bytes > before)
                {
                    throw new ArgumentOutOfRangeException(nameof(bytes), "A retained claim cannot grow without admission.");
                }

                if (Interlocked.CompareExchange(ref _bytes, bytes, before) == before)
                {
                    budget.Release(before - bytes);
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(Interlocked.Exchange(ref _bytes, 0));
            }
        }
    }
}
