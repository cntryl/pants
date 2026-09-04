namespace Cntryl.Pants.Support.Failpoints;

sealed class WalRollbackFailureFailpointHandler : IFailpointHandler
{
    int _appendFailureArmed = 1;
    int _rollbackFailureArmed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.AfterWalAppend &&
            Interlocked.Exchange(ref _appendFailureArmed, 0) == 1)
        {
            throw new PantsNoSpaceException(
                $"Injected failure at {Failpoint.AfterWalAppend}.");
        }

        if (failpoint == Failpoint.BeforeWalRollback &&
            Interlocked.Exchange(ref _rollbackFailureArmed, 0) == 1)
        {
            throw new IOException($"Injected failure at {Failpoint.BeforeWalRollback}.");
        }
    }
}
