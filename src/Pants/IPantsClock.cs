namespace Cntryl.Pants;

public interface IPantsClock
{
    DateTimeOffset UtcNow { get; }
}
