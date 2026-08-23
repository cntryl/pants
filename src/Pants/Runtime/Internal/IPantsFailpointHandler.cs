namespace Cntryl.Pants.Runtime.Internal;

interface IPantsFailpointHandler
{
    void Hit(PantsFailpoint failpoint);
}
