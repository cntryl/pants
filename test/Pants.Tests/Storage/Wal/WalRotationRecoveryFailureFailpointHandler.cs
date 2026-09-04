namespace Cntryl.Pants.Storage.Wal;

sealed class WalRotationRecoveryFailureFailpointHandler : IFailpointHandler
{
    public void Hit(Failpoint failpoint)
    {
        if (failpoint is Failpoint.AfterWalRotationStreamDisposed or
            Failpoint.BeforeWalRotationRecoveryStreamReopen)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
