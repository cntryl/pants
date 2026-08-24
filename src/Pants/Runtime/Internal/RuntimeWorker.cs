using System.Threading.Channels;

namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeWorker : IAsyncDisposable, IRuntimeServiceMetrics
{
    static readonly TimeSpan DefaultDisposalTimeout = TimeSpan.FromSeconds(30);

    readonly Channel<RuntimeWorkerCommand> _commands;
    readonly CancellationTokenSource _lifetimeCancellation = new();
    readonly Task _loopTask;
    readonly TimeSpan _disposalTimeout;
    long _completed;
    int _disposed;
    long _enqueued;
    long _failures;
    int _inFlight;
    int _outstanding;
    int _queueDepth;

    public RuntimeWorker(int capacity, TimeSpan? disposalTimeout = null)
    {
        _disposalTimeout = disposalTimeout ?? DefaultDisposalTimeout;
        if (_disposalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposalTimeout));
        }

        _commands = Channel.CreateBounded<RuntimeWorkerCommand>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _loopTask = Task.Run(RunAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.WaitAsync(_disposalTimeout).ConfigureAwait(false);
            _lifetimeCancellation.Dispose();
        }
        catch (TimeoutException exception)
        {
            _ = _loopTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _lifetimeCancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new PantsTimeoutException(
                "Runtime worker disposal did not complete before its deadline.",
                exception);
        }
    }

    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public int InFlight => Volatile.Read(ref _inFlight);

    public int Outstanding => Volatile.Read(ref _outstanding);

    public long Enqueued => Volatile.Read(ref _enqueued);

    public long Completed => Volatile.Read(ref _completed);

    public long Failures => Volatile.Read(ref _failures);

    public async ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        var command = await EnqueueCoreAsync(
                operation,
                cancellationToken,
                cancellationToken)
            .ConfigureAwait(false);
        await command.Task.ConfigureAwait(false);
    }

    public async ValueTask EnqueueAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default) =>
        _ = await ScheduleAsync(operation, cancellationToken).ConfigureAwait(false);

    public async ValueTask<Task> ScheduleAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        var command = await EnqueueCoreAsync(
                operation,
                cancellationToken,
                default)
            .ConfigureAwait(false);
        return command.Task;
    }

    public ValueTask EnqueueAsync(Action operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return EnqueueAsync(
            _ =>
            {
                operation();
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    async ValueTask<RuntimeWorkerCommand> EnqueueCoreAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken admissionCancellationToken,
        CancellationToken executionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var command = new RuntimeWorkerCommand(operation, executionCancellationToken);
        var admitted = false;
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _commands.Writer.WriteAsync(command, admissionCancellationToken)
                .ConfigureAwait(false);
            admitted = true;
            Interlocked.Increment(ref _enqueued);
            return command;
        }
        catch (ChannelClosedException exception)
        {
            throw new PantsAbortedException("The runtime worker is closed.", exception);
        }
        finally
        {
            if (!admitted)
            {
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Decrement(ref _outstanding);
            }
        }
    }

    public ValueTask ExecuteAsync(Action operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(
            _ =>
            {
                operation();
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    async Task RunAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _inFlight);
            try
            {
                await command.ExecuteAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                if (command.Task.IsFaulted || command.Task.IsCanceled)
                {
                    Interlocked.Increment(ref _failures);
                }
                else
                {
                    Interlocked.Increment(ref _completed);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Decrement(ref _outstanding);
            }
        }
    }
}
