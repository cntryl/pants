namespace Cntryl.Pants.Observability;

sealed class WalAppendDelayFailpointHandler(TimeSpan delay) : IFailpointHandler
{
    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.MidWalAppend)
        {
            Thread.Sleep(delay);
        }
    }
}
