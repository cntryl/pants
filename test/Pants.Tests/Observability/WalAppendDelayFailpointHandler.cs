using System.Diagnostics;

namespace Cntryl.Pants.Observability;

sealed class WalAppendDelayFailpointHandler(TimeSpan delay) : IFailpointHandler
{
    long _flushStarted;

    public long FsyncWindowNanoseconds { get; private set; }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.MidWalAppend)
        {
            Thread.Sleep(delay);
        }

        if (failpoint == Failpoint.BeforeWalFlush)
        {
            _flushStarted = Stopwatch.GetTimestamp();
        }
        else if (failpoint == Failpoint.AfterWalFlush)
        {
            FsyncWindowNanoseconds = checked(Stopwatch.GetElapsedTime(_flushStarted).Ticks * 100);
        }
    }
}
