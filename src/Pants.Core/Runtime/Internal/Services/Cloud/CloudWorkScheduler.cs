namespace Cntryl.Pants.Runtime.Internal.Services.Cloud;

sealed class CloudWorkScheduler : IAsyncDisposable
{
    static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(25);
    static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(1);

    readonly Lock _gate = new();
    readonly CancellationTokenSource _lifetimeCancellation = new();
    readonly Func<CancellationToken, ValueTask<CloudWorkOutcome>> _operation;
    readonly TimeProvider _timeProvider;
    readonly RuntimeWorker _worker;
    bool _disposed;
    int _outstanding;
    Task? _pumpTask;
    bool _requested;

    public CloudWorkScheduler(
        RuntimeWorker worker,
        Func<CancellationToken, ValueTask> operation,
        TimeProvider? timeProvider = null)
        : this(
            worker,
            async cancellationToken =>
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return CloudWorkOutcome.Completed;
            },
            timeProvider)
    {
    }

    /// <param name="operation">
    ///     Work that reports whether it should run again at once (<see cref="CloudWorkOutcome.Continue" />)
    ///     or after the retry backoff (<see cref="CloudWorkOutcome.RetryLater" />) without failing.
    /// </param>
    public CloudWorkScheduler(
        RuntimeWorker worker,
        Func<CancellationToken, ValueTask<CloudWorkOutcome>> operation,
        TimeProvider? timeProvider = null)
    {
        _worker = worker;
        _operation = operation;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Outstanding => Volatile.Read(ref _outstanding);

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pumpTask;
        var cancel = false;
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _requested = false;
                cancel = true;
            }

            pumpTask = _pumpTask;
        }

        if (cancel)
        {
            _lifetimeCancellation.Cancel();
        }

        if (pumpTask is not null)
        {
            await pumpTask.ConfigureAwait(false);
        }

        if (cancel)
        {
            _lifetimeCancellation.Dispose();
        }
    }

    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _requested = true;
            if (_pumpTask is null)
            {
                Volatile.Write(ref _outstanding, 1);
                _pumpTask = Task.Run(RunAsync);
            }
        }
    }

    async Task RunAsync()
    {
        var retryDelay = InitialRetryDelay;
        while (TryTakeRequest())
        {
            var outcome = CloudWorkOutcome.RetryLater;
            try
            {
                await _worker.ExecuteAsync(
                        async cancellationToken =>
                            outcome = await _operation(cancellationToken).ConfigureAwait(false),
                        _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                CompleteDisposedPump();
                return;
            }
            catch (Exception)
            {
                outcome = CloudWorkOutcome.RetryLater;
            }

            if (outcome == CloudWorkOutcome.Completed)
            {
                retryDelay = InitialRetryDelay;
                continue;
            }

            if (!RequestRetry())
            {
                CompleteDisposedPump();
                return;
            }

            if (outcome == CloudWorkOutcome.Continue)
            {
                retryDelay = InitialRetryDelay;
                continue;
            }

            try
            {
                await Task.Delay(
                        retryDelay,
                        _timeProvider,
                        _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _lifetimeCancellation.IsCancellationRequested)
            {
                CompleteDisposedPump();
                return;
            }

            retryDelay = NextRetryDelay(retryDelay);
        }
    }

    bool RequestRetry()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _requested = true;
            return true;
        }
    }

    bool TryTakeRequest()
    {
        lock (_gate)
        {
            if (_disposed || !_requested)
            {
                _pumpTask = null;
                Volatile.Write(ref _outstanding, 0);
                return false;
            }

            _requested = false;
            return true;
        }
    }

    void CompleteDisposedPump()
    {
        lock (_gate)
        {
            _pumpTask = null;
            Volatile.Write(ref _outstanding, 0);
        }
    }

    static TimeSpan NextRetryDelay(TimeSpan current) =>
        current >= MaximumRetryDelay
            ? MaximumRetryDelay
            : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaximumRetryDelay.Ticks));
}
