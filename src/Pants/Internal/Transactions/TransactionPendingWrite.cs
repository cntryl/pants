namespace Pants;

internal sealed class TransactionPendingWrite
{
    public TransactionPendingWrite(
        byte[]? value,
        DateTimeOffset? ttlExpiry,
        bool isDelete,
        bool insertOnly,
        bool assertValue)
    {
        Value = value;
        ExpiryUtc = ttlExpiry;
        IsDelete = isDelete;
        InsertOnly = insertOnly;
        AssertValue = assertValue;
    }

    public byte[]? Value { get; }

    public DateTimeOffset? ExpiryUtc { get; }

    public bool IsDelete { get; }

    public bool InsertOnly { get; }

    public bool AssertValue { get; }
}
