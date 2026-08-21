namespace Pants;

internal sealed class RuntimeCommand<T> : IRuntimeCommand
{
    private readonly Func<PantsRuntimeState, ValueTask<T>> _operation;
    private readonly TaskCompletionSource<T> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RuntimeCommand(Func<PantsRuntimeState, ValueTask<T>> operation)
    {
        _operation = operation;
    }

    public Task<T> Task => _completion.Task;

    public async ValueTask ExecuteAsync(PantsRuntimeState state)
    {
        try
        {
            T result = await _operation(state).ConfigureAwait(false);
            _completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            Exception publicException = exception switch
            {
                OperationCanceledException => exception,
                PantsException => exception,
                IOException ioException => PantsException.FromIOException(ioException),
                UnauthorizedAccessException accessException => new PantsIOException(
                    accessException.Message,
                    accessException),
                _ => new PantsInternalException(
                    "An unexpected runtime failure occurred.",
                    exception)
            };
            if (publicException is PantsNoSpaceException)
            {
                state.NoSpaceEvents = checked(state.NoSpaceEvents + 1);
            }

            _completion.TrySetException(publicException);
        }
    }
}
