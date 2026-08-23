namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class DeferredCompactionRaceFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _compactionAdmission = CreateCompletion();
    readonly TaskCompletionSource _flushPublication = CreateCompletion();
    readonly ManualResetEventSlim _releaseCompaction = new(false);
    readonly ManualResetEventSlim _releaseFlush = new(false);
    readonly ManualResetEventSlim _releaseReset = new(false);
    readonly TaskCompletionSource _signalReset = CreateCompletion();
    int _compactionHit;
    int _flushHits;
    int _resetHit;

    public void Dispose()
    {
        _releaseCompaction.Set();
        _releaseFlush.Set();
        _releaseReset.Set();
        _releaseCompaction.Dispose();
        _releaseFlush.Dispose();
        _releaseReset.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        switch (failpoint)
        {
            case Failpoint.BeforeCompactionAdmission
                when Interlocked.CompareExchange(ref _compactionHit, 1, 0) == 0:
                Block(_compactionAdmission, _releaseCompaction, failpoint);
                break;
            case Failpoint.BeforeFlushPublication
                when Interlocked.Increment(ref _flushHits) == 2:
                Block(_flushPublication, _releaseFlush, failpoint);
                break;
            case Failpoint.BeforeDeferredCompactionSignalReset
                when Interlocked.CompareExchange(ref _resetHit, 1, 0) == 0:
                Block(_signalReset, _releaseReset, failpoint);
                break;
        }
    }

    public Task WaitForCompactionAdmissionAsync(TimeSpan timeout) =>
        _compactionAdmission.Task.WaitAsync(timeout);

    public Task WaitForFlushPublicationAsync(TimeSpan timeout) =>
        _flushPublication.Task.WaitAsync(timeout);

    public Task WaitForSignalResetAsync(TimeSpan timeout) =>
        _signalReset.Task.WaitAsync(timeout);

    public void ReleaseCompactionAdmission() => _releaseCompaction.Set();

    public void ReleaseFlushPublication() => _releaseFlush.Set();

    public void ReleaseSignalReset() => _releaseReset.Set();

    static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    static void Block(
        TaskCompletionSource entered,
        ManualResetEventSlim release,
        Failpoint failpoint)
    {
        entered.TrySetResult();
        if (!release.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }
}
