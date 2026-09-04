namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalRecord(
    uint ColumnFamilyId,
    WalOperation Operation,
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    byte[]? RangeEnd,
    ulong? TransactionId,
    ulong WriterEpoch);
