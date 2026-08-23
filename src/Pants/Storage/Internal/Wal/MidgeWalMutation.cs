namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record MidgeWalMutation(
    uint ColumnFamilyId,
    MidgeWalOperation Operation,
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    byte[]? RangeEnd);
