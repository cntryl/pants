namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class NthStartupResidueDeleteFailpointHandler(int failAtHit) :
    IFailpointHandler
{
    int _failureCount;
    int _hitCount;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeStartupResidueDelete ||
            Interlocked.Increment(ref _hitCount) != failAtHit)
        {
            return;
        }

        Interlocked.Increment(ref _failureCount);
        throw new IOException($"Injected startup residue delete failure at hit {failAtHit}.");
    }
}
