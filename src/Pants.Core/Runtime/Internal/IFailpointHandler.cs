namespace Cntryl.Pants.Runtime.Internal;

interface IFailpointHandler
{
    void Hit(Failpoint failpoint);

    void HitCancellable(Failpoint failpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Hit(failpoint);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
