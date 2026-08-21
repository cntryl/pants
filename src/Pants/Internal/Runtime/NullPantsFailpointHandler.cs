namespace Pants;

internal sealed class NullPantsFailpointHandler : IPantsFailpointHandler
{
    private NullPantsFailpointHandler()
    {
    }

    public static NullPantsFailpointHandler Instance { get; } = new();

    public void Hit(PantsFailpoint failpoint)
    {
    }
}
