namespace Cntryl.Pants.Tests.Transactions.Spill;

sealed class ThrowingTransactionCommitBoundaryFailpointHandler(
    Failpoint target) : IFailpointHandler
{
    int _armed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == target && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new IOException("Injected spilled transaction commit-boundary failure.");
        }
    }
}
