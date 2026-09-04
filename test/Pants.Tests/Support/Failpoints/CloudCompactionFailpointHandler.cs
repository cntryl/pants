namespace Cntryl.Pants.Support.Failpoints;

sealed class CloudCompactionFailpointHandler : IFailpointHandler
{
    readonly Lock _gate = new();
    Action? _beforeFailure;
    bool _shouldThrow;
    Failpoint? _target;

    public void Hit(Failpoint failpoint)
    {
        Action? beforeFailure;
        bool shouldThrow;
        lock (_gate)
        {
            if (_target != failpoint)
            {
                return;
            }

            _target = null;
            beforeFailure = _beforeFailure;
            _beforeFailure = null;
            shouldThrow = _shouldThrow;
            _shouldThrow = false;
        }

        beforeFailure?.Invoke();
        if (shouldThrow)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }
    }

    public void Arm(Failpoint target, Action? beforeFailure = null)
    {
        lock (_gate)
        {
            _target = target;
            _beforeFailure = beforeFailure;
            _shouldThrow = true;
        }
    }

    public void ArmCallback(Failpoint target, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _target = target;
            _beforeFailure = callback;
            _shouldThrow = false;
        }
    }
}
