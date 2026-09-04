namespace Cntryl.Pants.Transactions.Internal;

sealed class TransactionIntentOperation
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
        bool insertOnly,
        ulong? expirationUnixMilliseconds = null)
    {
        Ordinal = ordinal;
        Kind = kind;
        Family = family;
        Key = key;
        EndExclusive = endExclusive;
        Value = value;
        TimeToLive = timeToLive;
        ExpirationUnixMilliseconds = expirationUnixMilliseconds ??
                                     (expiryUtc is { } expiration
                                         ? UnixTimestamp.FromDateTimeOffset(expiration)
                                         : null);
        ExpiryUtc = expiryUtc ??
                    (expirationUnixMilliseconds is { } rawExpiration
                        ? UnixTimestamp.ToDateTimeOffsetSaturating(rawExpiration)
                        : null);
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

    public ulong? ExpirationUnixMilliseconds { get; }

    public bool InsertOnly { get; }
}
