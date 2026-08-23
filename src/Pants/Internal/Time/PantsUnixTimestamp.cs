namespace Pants;

internal static class PantsUnixTimestamp
{
    static readonly long MaximumDateTimeUnixMilliseconds =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    internal static ulong FromDateTimeOffset(DateTimeOffset value)
    {
        if (value.UtcTicks == DateTimeOffset.MaxValue.UtcTicks)
        {
            return ulong.MaxValue;
        }

        var unixMilliseconds = value.ToUnixTimeMilliseconds();
        return unixMilliseconds <= 0
            ? 0
            : checked((ulong)unixMilliseconds);
    }

    internal static DateTimeOffset ToDateTimeOffsetSaturating(ulong unixMilliseconds) =>
        unixMilliseconds >= checked((ulong)MaximumDateTimeUnixMilliseconds)
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds(checked((long)unixMilliseconds));

    internal static ulong? ExpirationFromTimeToLive(
        DateTimeOffset commitTime,
        TimeSpan? timeToLive)
    {
        if (timeToLive is null or { Ticks: 0 })
        {
            return null;
        }

        if (timeToLive.Value.Ticks > DateTimeOffset.MaxValue.UtcTicks - commitTime.UtcTicks)
        {
            return ulong.MaxValue;
        }

        var seconds = checked((ulong)(timeToLive.Value.Ticks / TimeSpan.TicksPerSecond));
        var milliseconds = seconds > ulong.MaxValue / 1_000
            ? ulong.MaxValue
            : seconds * 1_000;
        var commitMilliseconds = FromDateTimeOffset(commitTime);
        return commitMilliseconds > ulong.MaxValue - milliseconds
            ? ulong.MaxValue
            : commitMilliseconds + milliseconds;
    }

    internal static bool IsExpired(ulong? expirationUnixMilliseconds, DateTimeOffset now) =>
        expirationUnixMilliseconds is { } expiration &&
        expiration <= FromDateTimeOffset(now);
}
