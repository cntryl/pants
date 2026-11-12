using System.Runtime.ExceptionServices;

namespace Cntryl.Pants.Runtime.Internal;

/// <summary>Owns startup cancellation until one atomic runtime admission transfers ownership.</summary>
sealed class StartupScope : IDisposable
{
    static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromDays(1);
    readonly object _gate = new();
    readonly CancellationTokenSource _cancellation = new();
    readonly CancellationToken _callerCancellation;
    readonly CancellationTokenRegistration _registration;
    readonly OperationDeadline _deadline;
    readonly TaskCompletionSource<Exception> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _callbacksFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ITimer? _timer;
    bool _prepared;
    bool _accepted;
    bool _leasePublication;
    int _disposed;

    public StartupScope(TimeSpan? budget, TimeProvider time, CancellationToken cancellationToken)
    {
        _callerCancellation = cancellationToken;
        if (budget is { } finite)
        {
            var started = time.GetTimestamp();
            if (finite.TotalSeconds * time.TimestampFrequency > long.MaxValue - (double)started)
            {
                throw PantsException.InvalidArgument("Open timeout cannot be represented by a monotonic deadline.");
            }
            _deadline = OperationDeadline.FromStart(started, finite, time);
            _timer = time.CreateTimer(static state => ((StartupScope)state!).OnTimer(), this,
                ClampTimer(_deadline.Remaining), Timeout.InfiniteTimeSpan);
        }
        else
        {
            _deadline = OperationDeadline.Unbounded;
        }

        _registration = cancellationToken.Register(static state => ((StartupScope)state!).Cancel(true), this);
    }

    public CancellationToken Token => _cancellation.Token;
    public OperationDeadline Deadline => _deadline;
    public Task<Exception> Cancelled => _cancelled.Task;
    public Task<bool> Decision => _decision.Task;

    public void Reject() => _decision.TrySetResult(false);

    public void Check()
    {
        if (_deadline.IsExpired)
        {
            Cancel(false);
        }
        if (_cancelled.Task.IsCompletedSuccessfully)
        {
            ExceptionDispatchInfo.Capture(_cancelled.Task.Result).Throw();
        }
    }

    public void BeginLeasePublication()
    {
        Check();
        lock (_gate)
        {
            Check();
            if (_leasePublication)
            {
                throw new PantsInternalException("A startup lease publication is already unresolved.");
            }
            _leasePublication = true;
        }
    }

    public void ResolveLeasePublication()
    {
        lock (_gate)
        {
            _leasePublication = false;
        }
    }

    public void Prepared()
    {
        lock (_gate)
        {
            Check();
            _prepared = true;
        }
    }

    public Actor Accept(Func<Actor?> preparedRuntime)
    {
        lock (_gate)
        {
            Check();
            if (!_prepared || _leasePublication)
            {
                throw new PantsInternalException("Startup cannot admit an incomplete runtime.");
            }
            var actor = preparedRuntime() ?? throw new PantsInternalException("Startup readiness has no prepared runtime.");
            if (!actor.HasPreparedStartupAuthority)
            {
                throw new PantsFencedException("Writer authority was lost before runtime admission.");
            }
            _accepted = true;
            _decision.TrySetResult(true);
            return actor;
        }
    }

    public void Cancel(bool callerCancelled)
    {
        lock (_gate)
        {
            if (_accepted || _cancelled.Task.IsCompleted)
            {
                return;
            }
            Exception failure = _leasePublication
                ? new PantsLeaseIndeterminateException("Startup ended while lease acquisition was unresolved.")
                : callerCancelled
                    ? new OperationCanceledException(_callerCancellation)
                    : new PantsTimeoutException("Aggregate database open deadline exhausted.");
            _cancelled.TrySetResult(failure);
            _decision.TrySetResult(false);
        }

        // Provider callbacks may ignore cancellation or block inside their registrations.
        // They remain owned work; the deadline waiter never runs or joins them.
        _ = CancelCallbacksAsync();
    }

    public Task WaitForCancellationCallbacksAsync() => _cancelled.Task.IsCompleted
        ? _callbacksFinished.Task
        : Task.CompletedTask;

    async Task CancelCallbacksAsync()
    {
        try
        {
            await Task.Run(() => _cancellation.CancelAsync()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation callback failure cannot replace the caller's startup outcome.
        }
        finally
        {
            _callbacksFinished.TrySetResult();
        }
    }

    void OnTimer()
    {
        if (_deadline.IsExpired)
        {
            Cancel(false);
        }
        else
        {
            try
            {
                _timer?.Change(ClampTimer(_deadline.Remaining), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                // An accepted or abandoned owner's disposal can overtake this timer callback.
            }
        }
    }

    static TimeSpan ClampTimer(TimeSpan remaining) => remaining < MaximumTimerDelay ? remaining : MaximumTimerDelay;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _registration.Dispose();
        _timer?.Dispose();
        _cancellation.Dispose();
    }
}
