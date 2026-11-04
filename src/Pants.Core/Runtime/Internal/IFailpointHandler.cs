namespace Cntryl.Pants.Runtime.Internal;

interface IFailpointHandler
{
    void Hit(Failpoint failpoint);

    void BackgroundWriteRateWait(int bytes, long startedAt, long until) =>
        Hit(Failpoint.BeforeBackgroundWriteRateWait);

    void HitCancellable(Failpoint failpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Hit(failpoint);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
