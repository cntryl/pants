namespace Cntryl.Pants;

internal sealed class SystemPantsClock : IPantsClock
{
    public static SystemPantsClock Instance { get; } = new();

    private SystemPantsClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
