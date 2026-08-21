namespace Pants;

internal sealed class TransactionIntentOperation
{
    public TransactionIntentOperation(
        ulong ordinal,
        CommitOperationKind kind,
        ColumnFamilyIdentity family,
        byte[] key,
        byte[]? endExclusive,
        byte[]? value,
        TimeSpan? timeToLive,
        DateTimeOffset? expiryUtc,
        bool insertOnly)
    {
        Ordinal = ordinal;
        Kind = kind;
        Family = family;
        Key = key;
        EndExclusive = endExclusive;
        Value = value;
        TimeToLive = timeToLive;
        ExpiryUtc = expiryUtc;
        InsertOnly = insertOnly;
    }

    public ulong Ordinal { get; }

    public CommitOperationKind Kind { get; }

    public ColumnFamilyIdentity Family { get; }

    public byte[] Key { get; }

    public byte[]? EndExclusive { get; }

    public byte[]? Value { get; }

    public TimeSpan? TimeToLive { get; }

    public DateTimeOffset? ExpiryUtc { get; }

    public bool InsertOnly { get; }
}
