namespace Pants;

internal sealed record TransactionSpillRunHeader(
    int RecordCount,
    ulong OrdinalTableOffset,
    ulong SparseIndexOffset,
    int SparseCount,
    ulong FileLength);
