namespace Cntryl.Pants.Runtime.Internal;

sealed class CommitRuntimeCommand : IRuntimeCommand
{
    readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly Func<RuntimeState, ValueTask<bool>> _operation;

    public CommitRuntimeCommand(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        Func<RuntimeState, ValueTask<bool>> operation)
    {
        WriteOptions = writeOptions;
        Payload = payload;
        _operation = operation;
    }

    public PantsWriteOptions WriteOptions { get; }

    public CommitPayload Payload { get; }

    public Task<bool> Task => _completion.Task;

    public async ValueTask ExecuteAsync(RuntimeState state)
    {
        try
        {
            Complete(await _operation(state).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            Fail(state, exception);
        }
    }

    public void Complete(bool result) => _completion.TrySetResult(result);

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

        _completion.TrySetException(publicException);
    }
}
