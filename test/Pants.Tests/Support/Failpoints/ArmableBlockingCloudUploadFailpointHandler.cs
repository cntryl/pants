namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class ArmableBlockingCloudUploadFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _release = new(false);
    int _armed;
    int _hit;

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeCloudUpload ||
            Volatile.Read(ref _armed) == 0 ||
            Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
        {
            return;
        }

        _entered.TrySetResult();
        if (!_release.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public Task WaitUntilEnteredAsync(TimeSpan timeout) => _entered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();
}
