namespace Pants;

internal sealed record TransactionSpillRun(string Path, string RangePath, int RecordCount);
