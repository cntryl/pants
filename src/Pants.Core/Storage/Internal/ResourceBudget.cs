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

    public long Limit { get; } = limit;

    public long Current => Interlocked.Read(ref _current);

    public long Peak => Interlocked.Read(ref _peak);

    public IDisposable Reserve(long bytes) =>
        TryReserve(bytes, out var reservation, out var before)
            ? reservation
            : throw PantsException.ResourceLimit(
                $"Reserving {bytes} bytes would exceed the {Limit}-byte resource budget " +
                $"({before} bytes already reserved).");

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

    sealed class Reservation(ResourceBudget budget, long bytes) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(bytes);
            }
        }
    }
}
