namespace Cntryl.Pants.Runtime.Internal;

sealed class CellState
{
    public CellState(byte[]? value, ulong writeSequence, DateTimeOffset? expiresAtUtc)
        : this(
            value,
            writeSequence,
            expiresAtUtc is { } expiration
                ? UnixTimestamp.FromDateTimeOffset(expiration)
                : null)
    {
    }

    CellState(byte[]? value, ulong writeSequence, ulong? expirationUnixMilliseconds)
    {
        Value = value;
        WriteSequence = writeSequence;
        ExpirationUnixMilliseconds = expirationUnixMilliseconds;
    }

    public byte[]? Value { get; }

    public ulong WriteSequence { get; }

    public ulong? ExpirationUnixMilliseconds { get; }

    public bool IsExpired(DateTimeOffset now) =>
        UnixTimestamp.IsExpired(ExpirationUnixMilliseconds, now);

    internal static CellState FromUnixMilliseconds(
        byte[]? value,
        ulong writeSequence,
        ulong? expirationUnixMilliseconds) =>
        new(value, writeSequence, expirationUnixMilliseconds);
}
