namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class ArmableFailpointHandler : IFailpointHandler
{
    readonly Lock _gate = new();
    Failpoint? _target;

    public void Hit(Failpoint failpoint)
    {
        lock (_gate)
        {
            if (_target != failpoint)
            {
                return;
            }

            _target = null;
        }

        throw new IOException($"Injected failure at {failpoint}.");
    }

    public void Arm(Failpoint target)
    {
        lock (_gate)
        {
            _target = target;
        }
    }
}
