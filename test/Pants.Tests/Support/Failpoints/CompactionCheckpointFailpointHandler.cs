namespace Cntryl.Pants.Support.Failpoints;

sealed class CompactionCheckpointFailpointHandler : IFailpointHandler
{
    int _checkpointHit;
    int _publicationObserved;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.AfterCompactionManifestPublish)
        {
            Volatile.Write(ref _publicationObserved, 1);
            return;
        }

        if (failpoint == Failpoint.BeforeManifestCheckpointReplace &&
            Volatile.Read(ref _publicationObserved) != 0 &&
            Interlocked.CompareExchange(ref _checkpointHit, 1, 0) == 0)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
