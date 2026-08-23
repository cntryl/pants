namespace Cntryl.Pants;

sealed class RuntimeServiceCommand<TRequest, TResult>(TRequest request)
{
    readonly TaskCompletionSource<TResult> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TRequest Request { get; } = request;

    public Task<TResult> Response => _completion.Task;

    public void Complete(TResult result) => _completion.TrySetResult(result);

    public void Cancel(CancellationToken cancellationToken) =>
        _completion.TrySetCanceled(cancellationToken);

    public void Fail(Exception exception) => _completion.TrySetException(exception);
}
