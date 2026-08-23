namespace Cntryl.Pants.Transactions.Internal;

sealed record TransactionSpillRun(string Path, string RangePath, int RecordCount);
