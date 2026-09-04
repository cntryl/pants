namespace Cntryl.Pants.Support.Failpoints;

sealed class NoSpaceFlushFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _armed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeFlushBuild ||
            Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        _entered.TrySetResult();
        throw new PantsNoSpaceException($"No space at {failpoint}.");
    }

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => Volatile.Write(ref _armed, 0);
}
