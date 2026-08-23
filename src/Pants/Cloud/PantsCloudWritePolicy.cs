namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudWritePolicy(
    long EventualFlushSegmentGap,
    long WalSealMinimumSegmentBytes,
    TimeSpan WalSealMaximumFlushDelay,
    int WalSealMaximumPendingWrites)
{
    public PantsCloudWritePolicy()
        : this(128, 16 * 1024 * 1024, TimeSpan.FromMilliseconds(500), 10_000)
    {
    }
}
