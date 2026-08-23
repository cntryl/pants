namespace Cntryl.Pants.Tests.Support.TestDoubles;

sealed class ManualTimeProvider : TimeProvider
{
    long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public void Advance(TimeSpan elapsed) =>
        Interlocked.Add(ref _timestamp, elapsed.Ticks);
}
