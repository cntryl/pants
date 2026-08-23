namespace Cntryl.Pants.Tests;

sealed class CoalescedCommitSyncFailureFailpointHandler : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _runtimeBarrierRelease = new(initialState: false);
    int _runtimeBarrierArmed = 1;
    int _syncFailureArmed = 1;

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.BeforeRuntimeMetricsResponse &&
            Interlocked.Exchange(ref _runtimeBarrierArmed, 0) == 1)
        {
            _runtimeBarrierEntered.TrySetResult();
            if (!_runtimeBarrierRelease.Wait(MaximumBlockTime))
            {
                throw new TimeoutException(
                    $"Timed out waiting to release {PantsFailpoint.BeforeRuntimeMetricsResponse}.");
            }

            return;
        }

        if (failpoint == PantsFailpoint.BeforeCoalescedWalDurabilityBoundary &&
            Interlocked.Exchange(ref _syncFailureArmed, 0) == 1)
        {
            throw new PantsNoSpaceException(
                $"Injected failure at {PantsFailpoint.BeforeCoalescedWalDurabilityBoundary}.");
        }
    }

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _runtimeBarrierRelease.Dispose();
    }
}
