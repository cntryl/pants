namespace Cntryl.Pants.Runtime.Internal;

sealed class CommitRuntimeCommand : IRuntimeCommand
{
    readonly Func<RuntimeState, ValueTask<bool>> _operation;
    readonly RuntimeResponseSlot<bool> _response;

    public CommitRuntimeCommand(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        Func<RuntimeState, ValueTask<bool>> operation,
        RuntimeResponseRegistry registry,
        long requestId,
        string requestKind,
        OperationDeadline deadline = default)
    {
        WriteOptions = writeOptions;
        Payload = payload;
        _operation = operation;
        _response = new RuntimeResponseSlot<bool>(registry, requestId, requestKind);
        Deadline = deadline;
    }

    public PantsWriteOptions WriteOptions { get; }

    public CommitPayload Payload { get; }

    public OperationDeadline Deadline { get; }

    public Task<bool> Task => _response.Response;

    public async ValueTask ExecuteAsync(RuntimeState state)
    {
        try
        {
            var result = await _operation(state).ConfigureAwait(false);
            DisposePayload();
            Complete(result);
        }
        catch (Exception exception)
        {
            DisposePayload();
            Fail(state, exception);
        }
    }

    public void Complete(bool result) => _response.Complete(result);

    public void Fail(
        RuntimeState state,
        Exception exception,
        bool recordNoSpaceEvent = true)
    {
        var publicException = RuntimeExceptionMapper.ToPublicException(exception);
        if (recordNoSpaceEvent && RuntimeExceptionMapper.IsNoSpace(exception))
        {
            state.RecordNoSpaceEvent();
        }

        _response.Fail(publicException);
    }

    public bool AbandonResponse(TimeSpan timeout) => _response.Abandon(timeout);

    public void UnregisterResponse() => _response.Unregister();

    public void DisposePayload() => Payload.Dispose();
}
