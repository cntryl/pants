namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CompactionCheckpointFailpointHandler : IPantsFailpointHandler
{
    int _checkpointHit;
    int _publicationObserved;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.AfterCompactionManifestPublish)
        {
            Volatile.Write(ref _publicationObserved, 1);
            return;
        }

        if (failpoint == PantsFailpoint.BeforeManifestCheckpointReplace &&
            Volatile.Read(ref _publicationObserved) != 0 &&
            Interlocked.CompareExchange(ref _checkpointHit, 1, 0) == 0)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
