namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeCommand<T> : IRuntimeCommand
{
    readonly CancellationToken _callerCancellationToken;
    readonly Func<RuntimeState, ValueTask<T>> _operation;
    readonly RuntimeResponseSlot<T> _response;

    public RuntimeCommand(
        Func<RuntimeState, ValueTask<T>> operation,
        RuntimeResponseRegistry registry,
        long requestId,
        string requestKind,
        Action<T>? abandonedResponse,
        CancellationToken callerCancellationToken)
    {
        _operation = operation;
        _callerCancellationToken = callerCancellationToken;
        _response = new RuntimeResponseSlot<T>(
            registry,
            requestId,
            requestKind,
            abandonedResponse);
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

    public bool AbandonResponse(TimeSpan timeout) => _response.Abandon(timeout);
}
