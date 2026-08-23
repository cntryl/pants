namespace Pants;

sealed class CloudWorkScheduler(
    RuntimeWorker worker,
    Func<CancellationToken, ValueTask> operation,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(25);
    static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(1);

    readonly Lock _gate = new();
    readonly CancellationTokenSource _lifetimeCancellation = new();
    readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    Task? _pumpTask;
    int _outstanding;
    bool _requested;
    bool _disposed;

    public int Outstanding => Volatile.Read(ref _outstanding);

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

    async Task RunAsync()
    {
        var retryDelay = InitialRetryDelay;
        while (TryTakeRequest())
        {
            try
            {
                await worker.ExecuteAsync(operation, _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                CompleteDisposedPump();
                return;
            }
            catch (Exception)
            {
                if (!RequestRetry())
                {
                    CompleteDisposedPump();
                    return;
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
