namespace Cntryl.Pants.Tests.Transactions.Spill;

sealed class TransactionCommitBoundaryFailpointHandler(
    Failpoint directBoundary,
    Failpoint spilledBoundary) : IFailpointHandler
{
    int _directHits;
    int _spilledHits;

    public int DirectHits => Volatile.Read(ref _directHits);

    public int SpilledHits => Volatile.Read(ref _spilledHits);

    public void Hit(Failpoint failpoint)
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
