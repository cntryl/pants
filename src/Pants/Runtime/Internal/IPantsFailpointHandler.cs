namespace Cntryl.Pants;

internal interface IPantsFailpointHandler
{
    void Hit(PantsFailpoint failpoint);
}
