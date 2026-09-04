namespace Cntryl.Pants.Observability;

sealed class WalBoundaryMetricsFailpointHandler(
    Failpoint failure,
    TimeSpan appendDelay) : IFailpointHandler
{
    int _delayArmed = 1;
    int _failureArmed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.MidWalAppend &&
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
