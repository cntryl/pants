namespace Cntryl.Pants.Support.Failpoints;

sealed class RetryingCloudWalSealFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _failureObserved = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _retryObserved = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _attempts;
    int _failuresEnabled = 1;

    public int Attempts => Volatile.Read(ref _attempts);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeWalRotation)
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _attempts);
        _failureObserved.TrySetResult();
        if (attempt > 1)
        {
            _retryObserved.TrySetResult();
        }

        if (Volatile.Read(ref _failuresEnabled) != 0)
        {
            throw new IOException("Injected cloud WAL seal failure.");
        }
    }

    public Task WaitForFailureAsync(TimeSpan timeout) =>
        _failureObserved.Task.WaitAsync(timeout);

    public Task WaitForRetryAsync(TimeSpan timeout) =>
        _retryObserved.Task.WaitAsync(timeout);

    public void AllowSuccess() => Volatile.Write(ref _failuresEnabled, 0);
}
