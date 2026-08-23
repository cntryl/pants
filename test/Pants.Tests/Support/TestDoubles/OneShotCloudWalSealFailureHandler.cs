namespace Cntryl.Pants.Tests.Support.TestDoubles;

sealed class OneShotCloudWalSealFailureHandler(
    PantsFailpoint failure = PantsFailpoint.BeforeWalRotation,
    Func<Exception>? createFailure = null) : IPantsFailpointHandler
{
    readonly TaskCompletionSource _failureInjected = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _retryAttempted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != failure)
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt == 1)
        {
            _failureInjected.TrySetResult();
            throw createFailure?.Invoke() ??
                  new IOException($"Injected first cloud WAL seal failure at {failure}.");
        }

        _retryAttempted.TrySetResult();
    }

    public Task WaitUntilFailureInjectedAsync(TimeSpan timeout) =>
        _failureInjected.Task.WaitAsync(timeout);

    public Task WaitUntilRetryAttemptedAsync(TimeSpan timeout) =>
        _retryAttempted.Task.WaitAsync(timeout);
}
