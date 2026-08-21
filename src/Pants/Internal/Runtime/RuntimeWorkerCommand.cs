namespace Pants;

internal sealed class RuntimeWorkerCommand
{
    private readonly Func<CancellationToken, ValueTask> _operation;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RuntimeWorkerCommand(Func<CancellationToken, ValueTask> operation)
    {
        _operation = operation;
    }

    public Task Task => _completion.Task;

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operation(cancellationToken).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            _completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }
}
