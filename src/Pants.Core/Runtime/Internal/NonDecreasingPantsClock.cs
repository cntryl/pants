namespace Cntryl.Pants.Runtime.Internal;

/// <summary>
///     Wraps a wall clock so its readings never go backwards.
/// </summary>
/// <remarks>
///     This is <b>not</b> a monotonic clock and must never be used to measure elapsed time or to
///     decide lease fencing. A backward system-clock adjustment makes it stop advancing until real
///     time catches up, so a duration measured against it can stall indefinitely. Use
///     <see cref="TimeProvider.GetTimestamp" /> for anything that must expire.
///     Its purpose is narrow: wall-clock instants that are written into persisted records and
///     compared across processes should not regress.
/// </remarks>
sealed class NonDecreasingPantsClock : IPantsClock
{
    readonly IPantsClock _inner;
    long _latestUtcTicks;

    public NonDecreasingPantsClock(IPantsClock inner)
    {
        _inner = inner;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            var observed = _inner.UtcNow.UtcTicks;
            while (true)
            {
                var latest = Volatile.Read(ref _latestUtcTicks);
                if (observed <= latest)
                {
                    return new DateTimeOffset(latest, TimeSpan.Zero);
                }

                if (Interlocked.CompareExchange(ref _latestUtcTicks, observed, latest) == latest)
                {
                    return new DateTimeOffset(observed, TimeSpan.Zero);
                }
            }
        }
    }
}
