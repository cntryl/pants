namespace Pants;

internal sealed record MidgeSstEntry(
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    bool IsDelete);
