namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record MidgeSstEntry(
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    bool IsDelete);
