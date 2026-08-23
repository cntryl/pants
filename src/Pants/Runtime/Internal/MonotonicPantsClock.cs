namespace Cntryl.Pants.Runtime.Internal;

sealed class MonotonicPantsClock : IPantsClock
{
    readonly IPantsClock _inner;
    long _latestUtcTicks;

    public MonotonicPantsClock(IPantsClock inner)
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
