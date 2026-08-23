namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record MidgeWalRecord(
    uint ColumnFamilyId,
    MidgeWalOperation Operation,
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    byte[]? RangeEnd,
    ulong? TransactionId,
    ulong WriterEpoch);
