namespace Cntryl.Pants.Storage.Internal;

/// <summary>
/// Shared bounded-resource accounting for internal streaming pipelines (compaction's merge
/// buffers, a scan's k-way merge buffers) — mirrors Midge's <c>ResourceBudget</c>/
/// <c>ResourceReservation</c>. <see cref="Reserve"/> is RAII-style: it throws immediately if the
/// reservation would exceed <see cref="Limit"/>, and the returned <see cref="IDisposable"/>
/// releases it. This bounds a single streaming operation's transient memory independent of how
/// much data it is processing in total, rather than merely observing it after the fact.
/// </summary>
sealed class ResourceBudget(long limit)
{
    long _current;
    long _peak;

    public long Limit { get; } = limit;

    public long Current => Interlocked.Read(ref _current);

    public long Peak => Interlocked.Read(ref _peak);

    public IDisposable Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var before = Interlocked.Read(ref _current);
            var after = before + bytes;
            if (after > Limit)
            {
                throw PantsException.ResourceLimit(
                    $"Reserving {bytes} bytes would exceed the {Limit}-byte resource budget " +
                    $"({before} bytes already reserved).");
            }

            if (Interlocked.CompareExchange(ref _current, after, before) == before)
            {
                UpdatePeak(after);
                return new Reservation(this, bytes);
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
