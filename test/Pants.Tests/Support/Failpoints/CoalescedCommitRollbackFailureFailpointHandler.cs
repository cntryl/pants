namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CoalescedCommitRollbackFailureFailpointHandler : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _runtimeBarrierRelease = new(false);
    int _appendHits;
    int _runtimeBarrierArmed = 1;

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _runtimeBarrierRelease.Dispose();
    }

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

        if (failpoint == PantsFailpoint.AfterWalAppend &&
            Interlocked.Increment(ref _appendHits) == 2)
        {
            throw new PantsNoSpaceException(
                $"Injected failure at {PantsFailpoint.AfterWalAppend} hit 2.");
        }

        if (failpoint == PantsFailpoint.BeforeCoalescedWalRollback)
        {
            throw new IOException(
                $"Injected failure at {PantsFailpoint.BeforeCoalescedWalRollback}.");
        }
    }

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();
}
