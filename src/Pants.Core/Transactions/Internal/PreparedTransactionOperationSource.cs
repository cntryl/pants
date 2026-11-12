namespace Cntryl.Pants.Transactions.Internal;

/// <summary>One serialized preparation observation shared by every WAL and apply traversal.</summary>
sealed class PreparedTransactionOperationSource(ITransactionOperationSource source, DateTimeOffset commitTime)
    : ITransactionOperationSource
{
    public ulong Count => source.Count;

    public bool IsSpilled => source.IsSpilled;

    public void Validate() => ForEach(static _ => { });

    public void ForEach(Action<TransactionIntentOperation> visitor) => source.ForEach(operation => visitor(new TransactionIntentOperation(
        operation.Ordinal,
        operation.Kind,
        operation.Family,
        operation.Key,
        operation.EndExclusive,
        operation.Value,
        null,
        null,
        operation.InsertOnly,
        operation.ExpirationUnixMilliseconds ?? UnixTimestamp.ExpirationFromTimeToLive(commitTime, operation.TimeToLive))));

    public TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key) => source.LatestBefore(ordinal, key);
}
