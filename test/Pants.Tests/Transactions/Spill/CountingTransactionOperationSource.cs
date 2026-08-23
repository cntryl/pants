namespace Cntryl.Pants.Tests;

internal sealed class CountingTransactionOperationSource(ITransactionOperationSource inner)
    : ITransactionOperationSource
{
    int _traversalCount;
    long _visitCount;
    int _latestBeforeCount;

    public ulong Count => inner.Count;

    public bool IsSpilled => inner.IsSpilled;

    public int TraversalCount => _traversalCount;

    public long VisitCount => _visitCount;

    public int LatestBeforeCount => _latestBeforeCount;

    public void Validate() => inner.Validate();

    public void ForEach(Action<TransactionIntentOperation> visitor)
    {
        _traversalCount++;
        inner.ForEach(operation =>
        {
            _visitCount++;
            visitor(operation);
        });
    }

    public TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key)
    {
        _latestBeforeCount++;
        return inner.LatestBefore(ordinal, key);
    }
}
