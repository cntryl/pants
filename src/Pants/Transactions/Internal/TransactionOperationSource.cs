namespace Pants;

internal sealed class TransactionOperationSource : ITransactionOperationSource
{
    readonly TransactionSpillStore? _spillStore;
    readonly TransactionIntentOperation[] _residentOperations;
    readonly DateTimeOffset _commitTime;

    public TransactionOperationSource(
        TransactionSpillStore? spillStore,
        IReadOnlyList<TransactionIntentOperation> residentOperations,
        ulong count,
        DateTimeOffset commitTime)
    {
        ArgumentNullException.ThrowIfNull(residentOperations);

        _spillStore = spillStore;
        _residentOperations = residentOperations.ToArray();
        Array.Sort(
            _residentOperations,
            static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        _commitTime = commitTime;
        Count = count;
    }

    public ulong Count { get; }

    public bool IsSpilled => _spillStore?.HasRuns == true;

    public void Validate() => ForEach(static _ => { });

    public void ForEach(Action<TransactionIntentOperation> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);

        var expectedOrdinal = 0UL;
        _spillStore?.ForEach(Visit);
        foreach (var operation in _residentOperations)
        {
            Visit(operation);
        }

        if (expectedOrdinal != Count)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction operation count does not match its ordinal frontier.");
        }

        void Visit(TransactionIntentOperation operation)
        {
            if (operation.Ordinal != expectedOrdinal || expectedOrdinal >= Count)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction operation ordinals are not contiguous.");
            }

            expectedOrdinal = checked(expectedOrdinal + 1);
            visitor(ApplyCommitTime(operation));
        }
    }

    public TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key)
    {
        var latest = _spillStore?.LatestBefore(ordinal, key);
        foreach (var operation in _residentOperations)
        {
            if (operation.Ordinal < ordinal)
            {
                TransactionIntentLookup.Consider(ref latest, operation, key);
            }
        }

        return latest;
    }

    TransactionIntentOperation ApplyCommitTime(TransactionIntentOperation operation) =>
        new(
            operation.Ordinal,
            operation.Kind,
            operation.Family,
            operation.Key.ToArray(),
            operation.EndExclusive?.ToArray(),
            operation.Value?.ToArray(),
            null,
            null,
            operation.InsertOnly,
            PantsUnixTimestamp.ExpirationFromTimeToLive(_commitTime, operation.TimeToLive));
}
