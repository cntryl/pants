namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>The system clock shifted by a fixed offset, so a test can age a lease without waiting.</summary>
sealed class OffsetPantsClock(TimeSpan offset) : IPantsClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + offset;
}
