namespace Cntryl.Pants.Transactions.Spill;

sealed record WalTestRecord(
    byte Operation,
    uint ColumnFamilyId,
    ulong Sequence,
    byte[] Key,
    byte[]? Value,
    ulong? Expiration,
    byte[]? RangeEnd,
    ulong? TransactionId,
    ulong WriterEpoch,
    byte? Compression,
    int PayloadLength);
