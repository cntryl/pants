namespace Cntryl.Pants.Runtime.Internal;

sealed class SystemPantsClock : IPantsClock
{
    SystemPantsClock()
    {
    }

    public static SystemPantsClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
