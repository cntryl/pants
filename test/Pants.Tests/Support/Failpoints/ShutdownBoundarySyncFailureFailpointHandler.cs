namespace Cntryl.Pants.Support.Failpoints;

/// <summary>
///     Fails the first WAL fsync attempted once shutdown reaches its durability boundary. Arming
///     on the boundary keeps the background buffered-sync timer from consuming the injected
///     failure before shutdown runs, which would fence the WAL early and let shutdown skip it.
/// </summary>
sealed class ShutdownBoundarySyncFailureFailpointHandler : IFailpointHandler
{
    int _armed;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.BeforeShutdownWalDurabilityBoundary)
        {
            Volatile.Write(ref _armed, 1);
            return;
        }

        if (failpoint == Failpoint.BeforeWalSync &&
            Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }
}
