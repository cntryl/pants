namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class NthCompactionPublicationFailpointHandler(int failAtHit) :
    IPantsFailpointHandler
{
    int _failureCount;
    int _hitCount;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public int HitCount => Volatile.Read(ref _hitCount);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.BeforeCompactionManifestPublish ||
            Interlocked.Increment(ref _hitCount) != failAtHit)
        {
            return;
        }

        Interlocked.Increment(ref _failureCount);
        throw new IOException($"Injected compaction publication failure at hit {failAtHit}.");
    }
}
