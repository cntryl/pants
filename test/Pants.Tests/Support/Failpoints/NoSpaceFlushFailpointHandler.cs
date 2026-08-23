namespace Cntryl.Pants.Tests;

sealed class NoSpaceFlushFailpointHandler : IPantsFailpointHandler
{
    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int _armed = 1;

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => Volatile.Write(ref _armed, 0);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeFlushBuild ||
            Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        _entered.TrySetResult();
        throw new PantsNoSpaceException($"No space at {failpoint}.");
    }
}
