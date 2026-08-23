namespace Pants.Tests;

internal sealed class ThrowingTransactionCommitBoundaryFailpointHandler(
    PantsFailpoint target) : IPantsFailpointHandler
{
    int _armed = 1;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == target && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new IOException("Injected spilled transaction commit-boundary failure.");
        }
    }
}
