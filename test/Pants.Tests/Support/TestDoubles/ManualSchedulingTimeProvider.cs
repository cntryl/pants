namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>
///     A monotonic time source that moves only when a test advances it. Timers created through it
///     fire synchronously, on the advancing thread, once their due instant is reached.
/// </summary>
sealed class ManualSchedulingTimeProvider : TimeProvider
{
    readonly object _gate = new();
    readonly List<ManualTimer> _timers = [];
    long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing every timer whose due instant is reached along the way.</summary>
    public void Advance(TimeSpan elapsed)
    {
        long target;
        lock (_gate)
        {
            target = _timestamp + elapsed.Ticks;
        }

        while (TakeNextDue(target) is { } due)
        {
            due.Invoke();
        }

        lock (_gate)
        {
            _timestamp = Math.Max(_timestamp, target);
        }
    }

    /// <summary>Advances to the earliest due timer at or before <paramref name="target"/> and returns it.</summary>
    ManualTimer? TakeNextDue(long target)
    {
        lock (_gate)
        {
            ManualTimer? next = null;
            foreach (var candidate in _timers)
            {
                if (candidate.DueTimestamp is { } due &&
                    due <= target &&
                    (next?.DueTimestamp is not { } nextDue || due < nextDue))
                {
                    next = candidate;
                }
            }

            if (next?.DueTimestamp is not { } fireAt)
            {
                return null;
            }

            _timestamp = Math.Max(_timestamp, fireAt);
            if (next.PeriodTicks is { } period && period > 0)
            {
                next.DueTimestamp = fireAt + period;
            }
            else
            {
                next.DueTimestamp = null;
            }

            return next;
        }
    }

    void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _timestamp + dueTime.Ticks;
            timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? null : period.Ticks;
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    void Unschedule(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.DueTimestamp = null;
            _timers.Remove(timer);
        }
    }

    sealed class ManualTimer(ManualSchedulingTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueTimestamp { get; set; }

        public long? PeriodTicks { get; set; }

        bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Invoke() => callback(state);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Unschedule(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
