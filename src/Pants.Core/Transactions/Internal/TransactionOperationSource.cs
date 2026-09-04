namespace Cntryl.Pants.Transactions.Internal;

sealed class TransactionOperationSource : ITransactionOperationSource, IDisposable
{
    readonly DateTimeOffset _commitTime;
    readonly TransactionIntentOperation[] _residentOperations;
    readonly TransactionSpillStore? _spillStore;
    readonly bool _ownsSpillStore;
    int _disposed;

    public TransactionOperationSource(
        TransactionSpillStore? spillStore,
        IReadOnlyList<TransactionIntentOperation> residentOperations,
        ulong count,
        DateTimeOffset commitTime,
        bool ownsSpillStore = false)
    {
        ArgumentNullException.ThrowIfNull(residentOperations);

        _spillStore = spillStore;
        _ownsSpillStore = ownsSpillStore;
        _residentOperations = residentOperations.ToArray();
        Array.Sort(
            _residentOperations,
            static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        _commitTime = commitTime;
        Count = count;
    }

    public ulong Count { get; }

    public bool IsSpilled => _spillStore?.HasRuns == true;

    public void Dispose()
    {
        if (_ownsSpillStore && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _spillStore?.Dispose();
        }
    }

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

    public ValueTask ForEachAsync(
        Func<TransactionIntentOperation, ValueTask> visitor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ForEach(operation =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            visitor(operation).AsTask().GetAwaiter().GetResult();
        });
        return ValueTask.CompletedTask;
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
            UnixTimestamp.ExpirationFromTimeToLive(_commitTime, operation.TimeToLive));
}
