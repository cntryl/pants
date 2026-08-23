namespace Cntryl.Pants.Runtime.Internal;

sealed class NullPantsFailpointHandler : IPantsFailpointHandler
{
    NullPantsFailpointHandler()
    {
    }

    public static NullPantsFailpointHandler Instance { get; } = new();

    public void Hit(PantsFailpoint failpoint)
    {
    }
}
