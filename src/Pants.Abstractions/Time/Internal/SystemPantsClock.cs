namespace Cntryl.Pants.Time.Internal;

sealed class SystemPantsClock : IPantsClock
{
    SystemPantsClock()
    {
    }

    public static SystemPantsClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
