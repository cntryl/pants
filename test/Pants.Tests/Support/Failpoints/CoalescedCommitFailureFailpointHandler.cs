namespace Cntryl.Pants.Tests;

sealed class CoalescedCommitFailureFailpointHandler(
    PantsFailpoint? failure = null,
    int failAtHit = 1) : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _runtimeBarrierRelease = new(initialState: false);
    int _runtimeBarrierArmed = 1;
    int _failureHits;

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

        if (failure.HasValue &&
            failpoint == failure.Value &&
            Interlocked.Increment(ref _failureHits) == failAtHit)
        {
            throw new PantsNoSpaceException($"Injected failure at {failpoint} hit {failAtHit}.");
        }
    }

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _runtimeBarrierRelease.Dispose();
    }
}
