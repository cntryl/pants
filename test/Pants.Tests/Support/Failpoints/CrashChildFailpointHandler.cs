namespace Cntryl.Pants.Tests;

sealed class CrashChildFailpointHandler(PantsFailpoint target) : IPantsFailpointHandler
{
    int _remainingFailures = 1;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == target && Interlocked.Exchange(ref _remainingFailures, 0) == 1)
        {
            throw new PantsIOException($"Injected failure at {failpoint}.");
        }
    }
}
