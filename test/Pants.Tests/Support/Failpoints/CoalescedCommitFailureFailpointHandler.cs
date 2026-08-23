namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CoalescedCommitFailureFailpointHandler(
    Failpoint? failure = null,
    int failAtHit = 1) : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _runtimeBarrierRelease = new(false);
    int _failureHits;
    int _runtimeBarrierArmed = 1;

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

        if (failure.HasValue &&
            failpoint == failure.Value &&
            Interlocked.Increment(ref _failureHits) == failAtHit)
        {
            throw new PantsNoSpaceException($"Injected failure at {failpoint} hit {failAtHit}.");
        }
    }

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();
}
