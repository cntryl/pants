namespace Cntryl.Pants.Checkpoint;

static class CheckpointClock
{
    public static long Deadline(TimeProvider time, long started, TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            return started;
        }
        var ticks = (decimal)budget.Ticks * time.TimestampFrequency / TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue - (decimal)started ? long.MaxValue : checked(started + (long)ticks);
    }

    public static TimeSpan Remaining(TimeProvider time, long deadline) =>
        deadline <= time.GetTimestamp() ? TimeSpan.Zero : time.GetElapsedTime(time.GetTimestamp(), deadline);

    public static ulong Nanoseconds(TimeSpan duration) =>
        duration.Ticks < 0 || (ulong)duration.Ticks > ulong.MaxValue / 100
            ? throw new InvalidOperationException("The monotonic measurement clock overflowed or moved backward.")
            : (ulong)duration.Ticks * 100;
}
