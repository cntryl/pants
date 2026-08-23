namespace Cntryl.Pants.Tests;

sealed class CloudCompactionFailpointHandler : IPantsFailpointHandler
{
    readonly Lock _gate = new();
    PantsFailpoint? _target;
    Action? _beforeFailure;
    bool _shouldThrow;

    public void Arm(PantsFailpoint target, Action? beforeFailure = null)
    {
        lock (_gate)
        {
            _target = target;
            _beforeFailure = beforeFailure;
            _shouldThrow = true;
        }
    }

    public void ArmCallback(PantsFailpoint target, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _target = target;
            _beforeFailure = callback;
            _shouldThrow = false;
        }
    }

    public void Hit(PantsFailpoint failpoint)
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
}
