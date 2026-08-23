namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class PersistentThrowingFlushFailpointHandler(PantsFailpoint target) :
    IPantsFailpointHandler
{
    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _armed = 1;
    int _hits;

    public int HitCount => Volatile.Read(ref _hits);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != target || Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _hits);
        _entered.TrySetResult();
        throw new IOException($"Injected persistent failure at {failpoint}.");
    }

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => Volatile.Write(ref _armed, 0);
}
