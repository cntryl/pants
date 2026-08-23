namespace Cntryl.Pants.Tests.Storage.Wal;

sealed class WalRotationRecoveryFailureFailpointHandler : IPantsFailpointHandler
{
    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint is PantsFailpoint.AfterWalRotationStreamDisposed or
            PantsFailpoint.BeforeWalRotationRecoveryStreamReopen)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
