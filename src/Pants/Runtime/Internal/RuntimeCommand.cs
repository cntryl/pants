namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeCommand<T> : IRuntimeCommand
{
    readonly CancellationToken _callerCancellationToken;
    readonly Func<RuntimeState, ValueTask<T>> _operation;
    readonly RuntimeResponseSlot<T> _response = new();

    public RuntimeCommand(
        Func<RuntimeState, ValueTask<T>> operation,
        CancellationToken callerCancellationToken)
    {
        _operation = operation;
        _callerCancellationToken = callerCancellationToken;
    }

    public Task<T> Response => _response.Response;

    public async ValueTask ExecuteAsync(RuntimeState state)
    {
        try
        {
            var result = await _operation(state).ConfigureAwait(false);
            _response.Complete(result);
        }
        catch (Exception exception)
        {
            var publicException = RuntimeExceptionMapper.ToPublicException(
                exception,
                _callerCancellationToken);
            if (publicException is PantsNoSpaceException)
            {
                state.RecordNoSpaceEvent();
            }

            _response.Fail(publicException);
        }
    }

    public void UnregisterResponse() => _response.Unregister();
}
