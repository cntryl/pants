namespace Cntryl.Pants.Support.Failpoints;

sealed class CoalescedCommitPreparationFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TestTimeouts.Expected;

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _commitPreparationEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ManualResetEventSlim _runtimeBarrierRelease = new(false);
    readonly ManualResetEventSlim _commitPreparationRelease = new(false);
    int _runtimeBarrierArmed = 1;
    int _commitPreparationPauseArmed = 1;

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _commitPreparationRelease.Set();
        _runtimeBarrierRelease.Dispose();
        _commitPreparationRelease.Dispose();
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

        if (failpoint == Failpoint.AfterCoalescedCommitPreparation &&
            Interlocked.Exchange(ref _commitPreparationPauseArmed, 0) == 1)
        {
            _commitPreparationEntered.TrySetResult();
            if (!_commitPreparationRelease.Wait(MaximumBlockTime))
            {
                throw new TimeoutException(
                    $"Timed out waiting to release {Failpoint.AfterCoalescedCommitPreparation}.");
            }
        }
    }

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public async Task WaitForCommitPreparationAsync(TimeSpan timeout) =>
        await _commitPreparationEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();

    public void ReleaseCommitPreparation() => _commitPreparationRelease.Set();
}
