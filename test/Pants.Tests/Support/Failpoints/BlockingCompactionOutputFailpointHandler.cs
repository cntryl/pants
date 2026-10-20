namespace Cntryl.Pants.Support.Failpoints;

sealed class BlockingCompactionOutputFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _blocked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Blocked => _blocked.Task;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.AfterCompactionOutputDurable)
        {
            return;
        }

        _blocked.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }

    public void HitCancellable(Failpoint failpoint, CancellationToken cancellationToken)
    {
        if (failpoint != Failpoint.AfterCompactionOutputDurable)
        {
            return;
        }

        _blocked.TrySetResult();
        _release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
    }

    public Task WaitUntilBlockedAsync(TimeSpan timeout) => _blocked.Task.WaitAsync(timeout);

    public void Release() => _release.TrySetResult();
}
