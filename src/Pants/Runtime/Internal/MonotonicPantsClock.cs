namespace Cntryl.Pants;

internal sealed class MonotonicPantsClock : IPantsClock
{
    private readonly IPantsClock _inner;
    private long _latestUtcTicks;

    public MonotonicPantsClock(IPantsClock inner)
    {
        _inner = inner;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            long observed = _inner.UtcNow.UtcTicks;
            while (true)
            {
                long latest = Volatile.Read(ref _latestUtcTicks);
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
