namespace Cntryl.Pants.Support.Failpoints;

sealed class ArmableFailpointHandler : IFailpointHandler
{
    readonly Lock _gate = new();
    Failpoint? _target;
    Func<Failpoint, Exception>? _exceptionFactory;

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

        throw _exceptionFactory?.Invoke(failpoint) ??
              new IOException($"Injected failure at {failpoint}.");
    }

    public void Arm(Failpoint target, Func<Failpoint, Exception>? exceptionFactory = null)
    {
        lock (_gate)
        {
            _target = target;
            _exceptionFactory = exceptionFactory;
        }
    }
}
