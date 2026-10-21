namespace Cntryl.Pants.Support.Failpoints;

sealed class FlushPipelineFailpointHandler(
    Failpoint target,
    bool throwOnHit = false,
    bool armOnConstruction = true) : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TestTimeouts.Expected;

    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _release = new(false);
    int _hit;
    int _armed = armOnConstruction ? 1 : 0;

    public int HitCount => Volatile.Read(ref _hit);

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != target ||
            Volatile.Read(ref _armed) == 0 ||
            Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
        {
            return;
        }

        _entered.TrySetResult();
        if (throwOnHit)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }

        if (!_release.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }

    public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
        await _entered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();
}
