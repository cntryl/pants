namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class RetryingShutdownBoundaryFailpointHandler : IPantsFailpointHandler
{
    int _hits;

    public int HitCount => Volatile.Read(ref _hits);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeShutdownWalDurabilityBoundary)
        {
            return;
        }

        if (Interlocked.Increment(ref _hits) == 1)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
