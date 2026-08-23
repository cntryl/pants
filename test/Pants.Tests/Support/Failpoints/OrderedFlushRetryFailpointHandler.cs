namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class OrderedFlushRetryFailpointHandler : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _firstEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _releaseFirst = new(false);
    readonly ManualResetEventSlim _releaseSecond = new(false);

    readonly TaskCompletionSource _secondEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _hits;

    public void Dispose()
    {
        _releaseFirst.Set();
        _releaseSecond.Set();
        _releaseFirst.Dispose();
        _releaseSecond.Dispose();
    }

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeFlushManifestPublish)
        {
            return;
        }

        var hit = Interlocked.Increment(ref _hits);
        if (hit == 1)
        {
            _firstEntered.TrySetResult();
            if (!_releaseFirst.Wait(MaximumBlockTime))
            {
                throw new TimeoutException("Timed out waiting to release the first flush publication.");
            }

            throw new IOException("Injected failure for the oldest flush publication.");
        }

        if (hit == 2)
        {
            _secondEntered.TrySetResult();
            if (!_releaseSecond.Wait(MaximumBlockTime))
            {
                throw new TimeoutException("Timed out waiting to release the retry publication.");
            }
        }
    }

    public async Task WaitForFirstAsync(TimeSpan timeout) =>
        await _firstEntered.Task.WaitAsync(timeout);

    public async Task WaitForSecondAsync(TimeSpan timeout) =>
        await _secondEntered.Task.WaitAsync(timeout);

    public void ReleaseFirst() => _releaseFirst.Set();

    public void ReleaseSecond() => _releaseSecond.Set();
}
