namespace Cntryl.Pants;

internal sealed record MidgeWalMutation(
    uint ColumnFamilyId,
    MidgeWalOperation Operation,
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    byte[]? RangeEnd);
