namespace Cntryl.Pants.Tests;

sealed class NthStartupResidueDeleteFailpointHandler(int failAtHit) :
    IPantsFailpointHandler
{
    int _failureCount;
    int _hitCount;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeStartupResidueDelete ||
            Interlocked.Increment(ref _hitCount) != failAtHit)
        {
            return;
        }

        Interlocked.Increment(ref _failureCount);
        throw new IOException($"Injected startup residue delete failure at hit {failAtHit}.");
    }
}
