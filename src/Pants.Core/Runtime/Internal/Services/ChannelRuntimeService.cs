using System.Threading.Channels;

namespace Cntryl.Pants.Runtime.Internal.Services;

abstract class ChannelRuntimeService<TRequest, TResult> : IAsyncDisposable, IRuntimeServiceMetrics
{
    readonly Channel<RuntimeServiceCommand<TRequest, TResult>> _commands;
    readonly CancellationTokenSource _lifetimeCancellation = new();
    readonly Task _loopTask;
    long _completed;
    int _disposed;
    long _enqueued;
    long _failures;
    int _inFlight;
    int _outstanding;
    int _queueDepth;

    protected ChannelRuntimeService(int capacity)
    {
        _commands = Channel.CreateBounded<RuntimeServiceCommand<TRequest, TResult>>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _loopTask = Task.Run(RunAsync);
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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

    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public int InFlight => Volatile.Read(ref _inFlight);

    public int Outstanding => Volatile.Read(ref _outstanding);

    public long Enqueued => Volatile.Read(ref _enqueued);

    public long Completed => Volatile.Read(ref _completed);

    public long Failures => Volatile.Read(ref _failures);

    public async ValueTask<TResult> ExecuteAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await ScheduleAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.ConfigureAwait(false);
    }

    public async ValueTask<Task<TResult>> ScheduleAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var command = new RuntimeServiceCommand<TRequest, TResult>(request);
        var admitted = false;
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            admitted = true;
            Interlocked.Increment(ref _enqueued);
            return command.Response;
        }
        catch (ChannelClosedException exception)
        {
            throw new PantsAbortedException("The runtime service is closed.", exception);
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

    protected abstract ValueTask<TResult> DispatchAsync(
        TRequest request,
        CancellationToken cancellationToken);

    async Task RunAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _inFlight);
            try
            {
                try
                {
                    var result = await DispatchAsync(
                            command.Request,
                            _lifetimeCancellation.Token)
                        .ConfigureAwait(false);
                    command.Complete(result);
                    Interlocked.Increment(ref _completed);
                }
                catch (OperationCanceledException exception)
                {
                    command.Cancel(exception.CancellationToken);
                    Interlocked.Increment(ref _failures);
                }
                catch (Exception exception)
                {
                    command.Fail(exception);
                    Interlocked.Increment(ref _failures);
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
