namespace Cntryl.Pants.Runtime.Internal;

/// <summary>One bounded encoded-output schedule, used exclusively by owned maintenance workers.</summary>
sealed class BackgroundWriteLimiter(ulong bytesPerSecond, TimeProvider timeProvider, Action<int, long, long>? waitObserved = null)
{
    readonly object _gate = new();
    long _next;
    long _limitedBytes;
    int _urgent;
    int _draining;

    public ulong LimitedBytes => unchecked((ulong)Volatile.Read(ref _limitedBytes));

    public void SetUrgent(bool urgent) => Volatile.Write(ref _urgent, urgent ? 1 : 0);

    public void BeginDrain()
    {
        Volatile.Write(ref _draining, 1);
    }

    public void Acquire(int bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes == 0 || IsUrgent)
        {
            return;
        }
        var numerator = (UInt128)(uint)bytes * (ulong)timeProvider.TimestampFrequency;
        var interval = (numerator + bytesPerSecond - 1) / bytesPerSecond;
        long until;
        long start;
        lock (_gate)
        {
            start = Math.Max(_next, timeProvider.GetTimestamp());
            if (interval > (UInt128)(ulong)(long.MaxValue - start))
            {
                throw PantsException.ResourceLimit("The background write rate exceeds the supported monotonic interval.");
            }
            until = checked(start + (long)interval);
            _next = until;
        }
        waitObserved?.Invoke(bytes, start, until);
        while (timeProvider.GetTimestamp() < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsUrgent)
            {
                lock (_gate)
                {
                    _next = timeProvider.GetTimestamp();
                }
                return;
            }
            Thread.Sleep(1);
        }
        Interlocked.Add(ref _limitedBytes, bytes);
    }

    bool IsUrgent => Volatile.Read(ref _urgent) != 0 || Volatile.Read(ref _draining) != 0;
}
