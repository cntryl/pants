namespace Pants;

sealed class CloudFlushRetryScheduler : IAsyncDisposable
{
    static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(10);
    static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(250);

    readonly Lock _gate = new();
    readonly Dictionary<ColumnFamilyIdentity, Task> _retries = [];
    readonly CancellationTokenSource _lifetimeCancellation = new();
    long _retryAttempts;
    bool _disposed;

    public long RetryAttempts => Volatile.Read(ref _retryAttempts);

    public void Schedule(
        ColumnFamilyIdentity identity,
        Func<CancellationToken, ValueTask> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retries.ContainsKey(identity))
            {
                return;
            }

            _retries.Add(identity, RetryAsync(identity, operation));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] retries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            retries = _retries.Values.ToArray();
        }

        await Task.WhenAll(retries).ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
    }

    async Task RetryAsync(
        ColumnFamilyIdentity identity,
        Func<CancellationToken, ValueTask> operation)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        var delay = InitialDelay;
        try
        {
            while (true)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _retryAttempts);
                try
                {
                    await operation(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (IsRetryable(exception))
                {
                    delay = TimeSpan.FromMilliseconds(Math.Min(
                        delay.TotalMilliseconds * 2,
                        MaximumDelay.TotalMilliseconds));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                _retries.Remove(identity);
            }
        }
    }

    static bool IsRetryable(Exception exception) =>
        exception is PantsIOException or PantsTimeoutException;
}
