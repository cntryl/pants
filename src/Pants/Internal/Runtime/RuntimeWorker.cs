using System.Threading.Channels;

namespace Pants;

internal sealed class RuntimeWorker : IAsyncDisposable
{
    private readonly Channel<RuntimeWorkerCommand> _commands;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Task _loopTask;
    private int _queueDepth;
    private int _inFlight;
    int _outstanding;
    private int _disposed;
    private long _enqueued;
    private long _completed;
    private long _failures;

    public RuntimeWorker(int capacity)
    {
        _commands = Channel.CreateBounded<RuntimeWorkerCommand>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _loopTask = Task.Run(RunAsync);
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
        RuntimeWorkerCommand command = await EnqueueCoreAsync(operation, cancellationToken)
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
        var command = await EnqueueCoreAsync(operation, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<RuntimeWorkerCommand> EnqueueCoreAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var command = new RuntimeWorkerCommand(operation);
        bool admitted = false;
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        await _loopTask.ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
    }

    private async Task RunAsync()
    {
        await foreach (RuntimeWorkerCommand command in _commands.Reader.ReadAllAsync())
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
