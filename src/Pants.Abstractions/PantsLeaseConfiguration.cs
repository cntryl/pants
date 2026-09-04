namespace Cntryl.Pants;

public sealed record PantsLeaseConfiguration(
    TimeSpan TimeToLive,
    TimeSpan ClockSkewTolerance,
    ulong MinimumEpoch = 0,
    Action? LossCallback = null)
{
    public static PantsLeaseConfiguration Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(15));
}
