namespace Cntryl.Pants.Tests;

sealed class DdlFailpointHandler(params string[] targets) : IPantsFailpointHandler
{
    readonly Lock _gate = new();
    readonly HashSet<string> _remaining = new(targets, StringComparer.Ordinal);

    public void Hit(PantsFailpoint failpoint)
    {
        lock (_gate)
        {
            if (_remaining.Remove(failpoint.ToString()))
            {
                throw new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
