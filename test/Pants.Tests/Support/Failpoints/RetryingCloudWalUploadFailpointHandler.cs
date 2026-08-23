namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class RetryingCloudWalUploadFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _failed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _failureCount;
    int _failuresEnabled = 1;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeCloudWalUpload ||
            Volatile.Read(ref _failuresEnabled) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _failureCount);
        _failed.TrySetResult();
        throw new IOException($"Injected failure at {failpoint}.");
    }

    public Task WaitForFailureAsync(CancellationToken cancellationToken) =>
        _failed.Task.WaitAsync(cancellationToken);

    public void AllowSuccess() => Volatile.Write(ref _failuresEnabled, 0);
}
