namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class DdlFailpointHandler(params string[] targets) : IFailpointHandler
{
    readonly Lock _gate = new();
    readonly HashSet<string> _remaining = new(targets, StringComparer.Ordinal);

    public void Hit(Failpoint failpoint)
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
