namespace Pants.Tests;

internal sealed class TransactionCommitBoundaryFailpointHandler(
    PantsFailpoint directBoundary,
    PantsFailpoint spilledBoundary) : IPantsFailpointHandler
{
    int _directHits;
    int _spilledHits;

    public int DirectHits => Volatile.Read(ref _directHits);

    public int SpilledHits => Volatile.Read(ref _spilledHits);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == directBoundary)
        {
            Interlocked.Increment(ref _directHits);
        }

        if (failpoint != spilledBoundary)
        {
            return;
        }

        Interlocked.Increment(ref _spilledHits);
    }
}
