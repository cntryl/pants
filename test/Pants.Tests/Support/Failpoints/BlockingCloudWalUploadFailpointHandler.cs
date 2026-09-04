namespace Cntryl.Pants.Support.Failpoints;

sealed class BlockingCloudWalUploadFailpointHandler : IFailpointHandler, IDisposable
{
    readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _release = new(false);
    int _hit;

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeCloudWalUpload ||
            Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
        {
            return;
        }

        _entered.TrySetResult();
        if (!_release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Timed out waiting to release cloud WAL upload.");
        }
    }

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();
}
