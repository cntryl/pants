namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class WalRollbackFailureFailpointHandler : IPantsFailpointHandler
{
    int _appendFailureArmed = 1;
    int _rollbackFailureArmed = 1;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.AfterWalAppend &&
            Interlocked.Exchange(ref _appendFailureArmed, 0) == 1)
        {
            throw new PantsNoSpaceException(
                $"Injected failure at {PantsFailpoint.AfterWalAppend}.");
        }

        if (failpoint == PantsFailpoint.BeforeWalRollback &&
            Interlocked.Exchange(ref _rollbackFailureArmed, 0) == 1)
        {
            throw new IOException($"Injected failure at {PantsFailpoint.BeforeWalRollback}.");
        }
    }
}
