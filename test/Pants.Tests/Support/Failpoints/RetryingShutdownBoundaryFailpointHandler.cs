namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class RetryingShutdownBoundaryFailpointHandler : IFailpointHandler
{
    int _hits;

    public int HitCount => Volatile.Read(ref _hits);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeShutdownWalDurabilityBoundary)
        {
            return;
        }

        if (Interlocked.Increment(ref _hits) == 1)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
