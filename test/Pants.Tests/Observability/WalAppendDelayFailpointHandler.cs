namespace Pants.Tests;

sealed class WalAppendDelayFailpointHandler(TimeSpan delay) : IPantsFailpointHandler
{
    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.MidWalAppend)
        {
            Thread.Sleep(delay);
        }
    }
}
