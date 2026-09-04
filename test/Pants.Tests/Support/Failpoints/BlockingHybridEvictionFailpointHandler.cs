namespace Cntryl.Pants.Support.Failpoints;

sealed class BlockingHybridEvictionFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _blocked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _armed;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeHybridSstEviction ||
            Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return;
        }

        _blocked.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public Task WaitUntilBlockedAsync(TimeSpan timeout) => _blocked.Task.WaitAsync(timeout);

    public void Release() => _release.TrySetResult();
}
