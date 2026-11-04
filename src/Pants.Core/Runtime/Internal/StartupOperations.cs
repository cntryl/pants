namespace Cntryl.Pants.Runtime.Internal;

/// <summary>Runs startup phases under the owner's one cancellation scope, without nested timer callbacks.</summary>
readonly struct StartupOperations(StartupScope? scope)
{
    public ValueTask RunMutationAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken);

    public ValueTask<T> RunMutationAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken);

    public async ValueTask RunAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        scope?.Check();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            scope?.Check();
            throw;
        }
        scope?.Check();
    }

    public async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        scope?.Check();
        cancellationToken.ThrowIfCancellationRequested();
        T result;
        try
        {
            result = await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            scope?.Check();
            throw;
        }
        // Return resource-producing results to their owner before the next phase checks the
        // deadline. Throwing here could discard an acquired store or lease before assignment.
        return result;
    }
}
