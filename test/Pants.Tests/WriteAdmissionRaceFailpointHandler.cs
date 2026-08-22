namespace Pants.Tests;

sealed class WriteAdmissionRaceFailpointHandler : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _flushEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _walEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _flushRelease = new(initialState: false);
    readonly ManualResetEventSlim _walRelease = new(initialState: false);
    int _flushHit;
    int _walArmed;
    int _walHit;

    public void ArmWalAppend() => Volatile.Write(ref _walArmed, 1);

    public async Task WaitForFlushAsync(TimeSpan timeout) =>
        await _flushEntered.Task.WaitAsync(timeout);

    public async Task WaitForWalAsync(TimeSpan timeout) =>
        await _walEntered.Task.WaitAsync(timeout);

    public void ReleaseFlush() => _flushRelease.Set();

    public void ReleaseWal() => _walRelease.Set();

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.BeforeFlushPublication &&
            Interlocked.CompareExchange(ref _flushHit, 1, 0) == 0)
        {
            Block(_flushEntered, _flushRelease, failpoint);
            return;
        }

        if (failpoint == PantsFailpoint.BeforeWalAppend &&
            Volatile.Read(ref _walArmed) != 0 &&
            Interlocked.CompareExchange(ref _walHit, 1, 0) == 0)
        {
            Block(_walEntered, _walRelease, failpoint);
        }
    }

    public void Dispose()
    {
        _flushRelease.Set();
        _walRelease.Set();
        _flushRelease.Dispose();
        _walRelease.Dispose();
    }

    static void Block(
        TaskCompletionSource entered,
        ManualResetEventSlim release,
        PantsFailpoint failpoint)
    {
        entered.TrySetResult();
        if (!release.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }
}
