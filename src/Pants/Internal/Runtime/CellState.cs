namespace Pants;

internal sealed class CellState
{
    public CellState(byte[]? value, long writeSequence, DateTimeOffset? expiresAtUtc)
    {
        Value = value;
        WriteSequence = writeSequence;
        ExpiresAtUtc = expiresAtUtc;
    }

    public byte[]? Value { get; }

    public long WriteSequence { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is not null && ExpiresAtUtc <= now;
}
