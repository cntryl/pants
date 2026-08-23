namespace Pants.Tests;

sealed class WalBoundaryMetricsFailpointHandler(
    PantsFailpoint failure,
    TimeSpan appendDelay) : IPantsFailpointHandler
{
    int _delayArmed = 1;
    int _failureArmed = 1;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.MidWalAppend &&
            Interlocked.Exchange(ref _delayArmed, 0) == 1)
        {
            Thread.Sleep(appendDelay);
        }

        if (failpoint == failure && Interlocked.Exchange(ref _failureArmed, 0) == 1)
        {
            throw new PantsIOException($"Injected failure at {failpoint}.");
        }
    }
}
