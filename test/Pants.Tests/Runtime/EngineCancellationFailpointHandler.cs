namespace Cntryl.Pants.Runtime;

sealed class EngineCancellationFailpointHandler : IFailpointHandler
{
    int _armed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.BeforeWalAppend &&
            Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new OperationCanceledException(
                $"Injected engine cancellation at {Failpoint.BeforeWalAppend}.");
        }
    }
}
