namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalMutation(
    uint ColumnFamilyId,
    WalOperation Operation,
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    byte[]? RangeEnd);
