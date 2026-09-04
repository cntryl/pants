namespace Cntryl.Pants.Support.Failpoints;

sealed class CoalescedCommitSyncFailureFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _runtimeBarrierRelease = new(false);
    int _runtimeBarrierArmed = 1;
    int _syncFailureArmed = 1;

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _runtimeBarrierRelease.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.BeforeRuntimeMetricsResponse &&
            Interlocked.Exchange(ref _runtimeBarrierArmed, 0) == 1)
        {
            _runtimeBarrierEntered.TrySetResult();
            if (!_runtimeBarrierRelease.Wait(MaximumBlockTime))
            {
                throw new TimeoutException(
                    $"Timed out waiting to release {Failpoint.BeforeRuntimeMetricsResponse}.");
            }

            return;
        }

        if (failpoint == Failpoint.BeforeCoalescedWalDurabilityBoundary &&
            Interlocked.Exchange(ref _syncFailureArmed, 0) == 1)
        {
            throw new PantsNoSpaceException(
                $"Injected failure at {Failpoint.BeforeCoalescedWalDurabilityBoundary}.");
        }
    }

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();
}
