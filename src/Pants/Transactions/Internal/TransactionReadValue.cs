namespace Cntryl.Pants.Transactions.Internal;

sealed record TransactionReadValue(byte[]? Value, bool Missing);
