namespace Cntryl.Pants.Support.Failpoints;

sealed class RunLoopFaultFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _faulted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _armed;
    int _armOnMetrics;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.BeforeRuntimeMetricsResponse &&
            Interlocked.Exchange(ref _armOnMetrics, 0) != 0)
        {
            Arm();
        }
        if (failpoint != Failpoint.AfterRuntimeCommandExecution ||
            Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        _faulted.TrySetResult();
        throw new InvalidOperationException($"Injected failure at {failpoint}.");
    }

    public async Task WaitUntilFaultedAsync(TimeSpan timeout) =>
        await _faulted.Task.WaitAsync(timeout);

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void ArmOnRuntimeMetricsResponse() => Volatile.Write(ref _armOnMetrics, 1);
}

sealed class GatedShutdownBoundaryFailpointHandler : IFailpointHandler, IDisposable
{
    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _release = new(false);
    int _hits;

    public int HitCount => Volatile.Read(ref _hits);

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeShutdownWalDurabilityBoundary)
        {
            return;
        }

        Interlocked.Increment(ref _hits);
        _entered.TrySetResult();
        if (!_release.Wait(TestTimeouts.Expected))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();
}
