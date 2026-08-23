namespace Pants.Tests;

sealed class CountingFailpointHandler(PantsFailpoint target) : IPantsFailpointHandler
{
    int _hitCount;

    public int HitCount => Volatile.Read(ref _hitCount);

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == target)
        {
            Interlocked.Increment(ref _hitCount);
        }
    }
}
