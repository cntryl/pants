namespace Cntryl.Pants.Tests.Transactions.Spill;

sealed class CountingTransactionOperationSource(ITransactionOperationSource inner)
    : ITransactionOperationSource
{
    public int TraversalCount { get; private set; }

    public long VisitCount { get; private set; }

    public int LatestBeforeCount { get; private set; }

    public ulong Count => inner.Count;

    public bool IsSpilled => inner.IsSpilled;

    public void Validate() => inner.Validate();

    public void ForEach(Action<TransactionIntentOperation> visitor)
    {
        TraversalCount++;
        inner.ForEach(operation =>
        {
            VisitCount++;
            visitor(operation);
        });
    }

    public TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key)
    {
        LatestBeforeCount++;
        return inner.LatestBefore(ordinal, key);
    }
}
