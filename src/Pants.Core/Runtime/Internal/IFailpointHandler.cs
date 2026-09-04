namespace Cntryl.Pants.Runtime.Internal;

interface IFailpointHandler
{
    void Hit(Failpoint failpoint);
}
