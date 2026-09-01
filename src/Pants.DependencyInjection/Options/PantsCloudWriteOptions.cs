namespace Cntryl.Pants.DependencyInjection.Options;

public sealed class PantsCloudWriteOptions
{
    public long EventualFlushSegmentGap { get; set; } = 128;

    public long WalSealMinimumSegmentBytes { get; set; } = 16 * 1024 * 1024;

    public TimeSpan WalSealMaximumFlushDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public int WalSealMaximumPendingWrites { get; set; } = 10_000;
}
