namespace Cntryl.Pants.Support.Failpoints;

sealed class DropPipelineRaceFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _dropAdmissionEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _dropAdmissionRelease = new(false);

    readonly TaskCompletionSource _flushPublicationEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _flushPublicationRelease = new(false);
    int _dropAdmissionHit;
    int _flushPublicationHit;

    public void Dispose()
    {
        _dropAdmissionRelease.Set();
        _flushPublicationRelease.Set();
        _dropAdmissionRelease.Dispose();
        _flushPublicationRelease.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        switch (failpoint)
        {
            case Failpoint.BeforeDropAdmission
                when Interlocked.CompareExchange(ref _dropAdmissionHit, 1, 0) == 0:
                Block(_dropAdmissionEntered, _dropAdmissionRelease, failpoint);
                break;
            case Failpoint.BeforeFlushPublication
                when Interlocked.CompareExchange(ref _flushPublicationHit, 1, 0) == 0:
                Block(_flushPublicationEntered, _flushPublicationRelease, failpoint);
                break;
        }
    }

    public async Task WaitForDropAdmissionAsync(TimeSpan timeout) =>
        await _dropAdmissionEntered.Task.WaitAsync(timeout);

    public async Task WaitForFlushPublicationAsync(TimeSpan timeout) =>
        await _flushPublicationEntered.Task.WaitAsync(timeout);

    public void ReleaseDropAdmission() => _dropAdmissionRelease.Set();

    public void ReleaseFlushPublication() => _flushPublicationRelease.Set();

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
