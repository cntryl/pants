namespace Cntryl.Pants.Tests;

internal sealed class RetentionMeasuringTransactionOperationSource(ITransactionOperationSource inner)
    : ITransactionOperationSource
{
    readonly List<WeakReference<byte[]>> _visitedKeys = [];

    public ulong Count => inner.Count;

    public bool IsSpilled => inner.IsSpilled;

    public int RetainedVisitedKeyCount { get; private set; }

    public void Validate() => inner.Validate();

    public void ForEach(Action<TransactionIntentOperation> visitor)
    {
        inner.ForEach(operation =>
        {
            _visitedKeys.Add(new WeakReference<byte[]>(operation.Key));
            visitor(operation);
            if (checked((ulong)_visitedKeys.Count) == Count)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                RetainedVisitedKeyCount = _visitedKeys.Count(reference =>
                    reference.TryGetTarget(out _));
            }
        });
    }

    public TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key) =>
        inner.LatestBefore(ordinal, key);
}
