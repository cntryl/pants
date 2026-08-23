namespace Cntryl.Pants.Transactions.Internal;

sealed record TransactionAssertion(byte[] Key, TransactionReadValue Expected);
