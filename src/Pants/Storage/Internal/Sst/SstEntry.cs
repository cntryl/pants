namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record SstEntry(
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    bool IsDelete);
