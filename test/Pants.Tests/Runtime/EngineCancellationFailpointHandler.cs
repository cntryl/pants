namespace Cntryl.Pants.Tests;

sealed class EngineCancellationFailpointHandler : IPantsFailpointHandler
{
    int _armed = 1;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.BeforeWalAppend &&
            Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new OperationCanceledException(
                $"Injected engine cancellation at {PantsFailpoint.BeforeWalAppend}.");
        }
    }
}
