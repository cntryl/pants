namespace Cntryl.Pants.Support.Failpoints;

sealed class HybridEvictionFailpointHandler(Failpoint target) : IFailpointHandler
{
    int _remainingFailures = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == target && Interlocked.Exchange(ref _remainingFailures, 0) == 1)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
