namespace Cntryl.Pants.Runtime.Internal;

sealed class NullPantsFailpointHandler : IFailpointHandler
{
    NullPantsFailpointHandler()
    {
    }

    public static NullPantsFailpointHandler Instance { get; } = new();

    public void Hit(Failpoint failpoint)
    {
    }
}
