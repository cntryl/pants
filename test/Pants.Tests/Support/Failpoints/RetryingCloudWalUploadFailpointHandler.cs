namespace Cntryl.Pants.Tests;

sealed class RetryingCloudWalUploadFailpointHandler : IPantsFailpointHandler
{
    readonly TaskCompletionSource _failed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int _failuresEnabled = 1;
    int _failureCount;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public Task WaitForFailureAsync(CancellationToken cancellationToken) =>
        _failed.Task.WaitAsync(cancellationToken);

    public void AllowSuccess() => Volatile.Write(ref _failuresEnabled, 0);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeCloudWalUpload ||
            Volatile.Read(ref _failuresEnabled) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _failureCount);
        _failed.TrySetResult();
        throw new IOException($"Injected failure at {failpoint}.");
    }
}
