namespace Cntryl.Pants.Runtime.Internal;

/// <summary>Signals one original budget without imposing the native timer's maximum duration.</summary>
sealed class OperationDeadlineCancellation : IDisposable
{
    static readonly TimeSpan MaximumTimerSlice = TimeSpan.FromDays(1);
    readonly Lock _gate = new();
    readonly OperationDeadline _deadline;
    readonly CancellationTokenSource _source;
    readonly ITimer _timer;
    bool _disposed;

    public OperationDeadlineCancellation(OperationDeadline deadline, TimeProvider timeProvider, CancellationToken caller)
    {
        _deadline = deadline;
        _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _timer = timeProvider.CreateTimer(static state => ((OperationDeadlineCancellation)state!).Tick(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Tick();
    }

    public CancellationToken Token => _source.Token;

    void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var remaining = _deadline.Remaining;
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining < MaximumTimerSlice ? remaining : MaximumTimerSlice, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may dispose this scope after the timer's final observation.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
        _source.Dispose();
    }
}
