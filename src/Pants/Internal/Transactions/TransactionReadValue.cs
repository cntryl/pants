namespace Pants;

internal sealed record TransactionReadValue(byte[]? Value, bool Missing);
