namespace Cntryl.Pants.Runtime.Internal;

sealed class CellState
{
    public CellState(byte[]? value, long writeSequence, DateTimeOffset? expiresAtUtc)
        : this(
            value,
            writeSequence,
            expiresAtUtc is { } expiration
                ? UnixTimestamp.FromDateTimeOffset(expiration)
                : null)
    {
    }

    CellState(byte[]? value, long writeSequence, ulong? expirationUnixMilliseconds)
    {
        Value = value;
        WriteSequence = writeSequence;
        ExpirationUnixMilliseconds = expirationUnixMilliseconds;
    }

    public byte[]? Value { get; }

    public long WriteSequence { get; }

    public ulong? ExpirationUnixMilliseconds { get; }

    public bool IsExpired(DateTimeOffset now) =>
        UnixTimestamp.IsExpired(ExpirationUnixMilliseconds, now);

    internal static CellState FromUnixMilliseconds(
        byte[]? value,
        long writeSequence,
        ulong? expirationUnixMilliseconds) =>
        new(value, writeSequence, expirationUnixMilliseconds);
}
