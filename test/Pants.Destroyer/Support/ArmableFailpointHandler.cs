using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Injects a single failure the first time a named <see cref="Failpoint"/>
/// fires, then disarms — the in-process analogue of a real process crash
/// for the failpoint-tier scenarios, mirroring the pattern already
/// established in <c>test/Pants.Tests/Support/Failpoints/ArmableFailpointHandler.cs</c>.
/// Used with <c>PantsDatabase.OpenForTestingAsync</c> to cut Pants at an
/// exact internal boundary (e.g. between a WAL sync and its ack) rather
/// than at an arbitrary point in wall-clock time.
/// </summary>
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

        throw new IOException($"Destroyer-injected failure at {failpoint}.");
    }

    public void Arm(Failpoint target)
    {
        lock (_gate)
        {
            _target = target;
        }
    }
}
