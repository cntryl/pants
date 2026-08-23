namespace Pants;

internal sealed record TransactionIntentLookup(
    ulong Ordinal,
    byte[]? Value,
    bool IsDeleted)
{
    public static void Consider(
        ref TransactionIntentLookup? latest,
        TransactionIntentOperation operation,
        ReadOnlySpan<byte> key)
    {
        var candidate = operation.Kind switch
        {
            CommitOperationKind.Put when operation.Key.AsSpan().SequenceEqual(key) =>
                new TransactionIntentLookup(operation.Ordinal, operation.Value, IsDeleted: false),
            CommitOperationKind.Delete when operation.Key.AsSpan().SequenceEqual(key) =>
                new TransactionIntentLookup(operation.Ordinal, null, IsDeleted: true),
            CommitOperationKind.DeleteRange when
                operation.EndExclusive is not null &&
                operation.Key.AsSpan().SequenceCompareTo(key) <= 0 &&
                key.SequenceCompareTo(operation.EndExclusive) < 0 =>
                new TransactionIntentLookup(operation.Ordinal, null, IsDeleted: true),
            _ => null
        };
        if (candidate is not null && (latest is null || candidate.Ordinal > latest.Ordinal))
        {
            latest = candidate;
        }
    }
}
