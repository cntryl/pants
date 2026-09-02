using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Cuts the manifest checkpoint replace step the first time it happens
/// after a compaction has published, so the checkpoint never durably lands
/// even though the compaction it was checkpointing did. Ported from the
/// same two-flag pattern as
/// <c>test/Pants.Tests/Support/Failpoints/CompactionCheckpointFailpointHandler.cs</c>.
/// </summary>
sealed class CompactionCheckpointCutFailpointHandler : IFailpointHandler
{
    int _publicationObserved;
    int _checkpointCut;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.AfterCompactionManifestPublish)
        {
            Volatile.Write(ref _publicationObserved, 1);
            return;
        }

        if (failpoint == Failpoint.BeforeManifestCheckpointReplace &&
            Volatile.Read(ref _publicationObserved) != 0 &&
            Interlocked.CompareExchange(ref _checkpointCut, 1, 0) == 0)
        {
            throw new IOException($"Destroyer-injected failure at {failpoint}.");
        }
    }
}
