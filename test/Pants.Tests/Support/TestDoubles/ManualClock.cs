namespace Cntryl.Pants.Support.TestDoubles;

sealed class ManualClock(DateTimeOffset initial) : IPantsClock
{
    public DateTimeOffset UtcNow { get; set; } = initial;
}
