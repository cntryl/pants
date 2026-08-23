namespace Cntryl.Pants.Tests;

sealed class ArmableFailpointHandler : IPantsFailpointHandler
{
    readonly Lock _gate = new();
    PantsFailpoint? _target;

    public void Arm(PantsFailpoint target)
    {
        lock (_gate)
        {
            _target = target;
        }
    }

    public void Hit(PantsFailpoint failpoint)
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
}
