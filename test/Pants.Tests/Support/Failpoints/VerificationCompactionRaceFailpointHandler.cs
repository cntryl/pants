namespace Cntryl.Pants.Tests;

sealed class VerificationCompactionRaceFailpointHandler : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _release = new(initialState: false);
    int _hit;

    public Task WaitUntilEnteredAsync(TimeSpan timeout) => _entered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeCompactionAdmission ||
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

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }
}
