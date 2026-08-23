namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeResponseSlot<T>
{
    TaskCompletionSource<T>? _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Response => Volatile.Read(ref _completion)?.Task ??
                               throw new InvalidOperationException("The runtime response slot is not registered.");

    public void Complete(T response) =>
        Interlocked.Exchange(ref _completion, null)?.TrySetResult(response);

    public void Fail(Exception exception) =>
        Interlocked.Exchange(ref _completion, null)?.TrySetException(exception);

    public void Unregister() =>
        _ = Interlocked.Exchange(ref _completion, null);
}
