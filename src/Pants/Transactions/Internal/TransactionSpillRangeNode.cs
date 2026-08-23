namespace Cntryl.Pants;

internal sealed record TransactionSpillRangeNode(
    ulong Ordinal,
    ulong Left,
    ulong Right,
    byte[] Start,
    byte[] End,
    byte[] MaximumEnd);
