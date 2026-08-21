namespace Pants;

public interface IPantsClock
{
    DateTimeOffset UtcNow { get; }
}
