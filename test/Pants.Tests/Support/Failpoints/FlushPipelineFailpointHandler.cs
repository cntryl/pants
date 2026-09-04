namespace Cntryl.Pants.Support.Failpoints;

sealed class FlushPipelineFailpointHandler(
    Failpoint target,
    bool throwOnHit = false) : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _release = new(false);
    int _hit;

    public int HitCount => Volatile.Read(ref _hit);

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != target || Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
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
