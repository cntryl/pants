namespace Cntryl.Pants;

internal sealed record TransactionAssertion(byte[] Key, TransactionReadValue Expected);
