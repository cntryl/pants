namespace Cntryl.Pants.Support.Failpoints;

sealed class CountingFailpointHandler(Failpoint target) : IFailpointHandler
{
    int _hitCount;

    public int HitCount => Volatile.Read(ref _hitCount);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == target)
        {
            Interlocked.Increment(ref _hitCount);
        }
    }
}
