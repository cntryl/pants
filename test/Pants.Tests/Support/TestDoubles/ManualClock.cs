namespace Cntryl.Pants.Tests;

internal sealed class ManualClock(DateTimeOffset initial) : IPantsClock
{
    public DateTimeOffset UtcNow { get; set; } = initial;
}
