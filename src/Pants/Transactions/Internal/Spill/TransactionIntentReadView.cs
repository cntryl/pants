namespace Cntryl.Pants;

internal sealed class TransactionIntentReadView : IDisposable
{
    readonly TransactionSpillStore? _store;
    readonly TransactionSpillRun[] _runs;
    readonly TransactionIntentOperation[] _residentOperations;
    int _disposed;

    public TransactionIntentReadView(
        IReadOnlyList<TransactionIntentOperation> residentOperations)
        : this(null, [], residentOperations)
    {
    }

    internal TransactionIntentReadView(
        TransactionSpillStore? store,
        TransactionSpillRun[] runs,
        IReadOnlyList<TransactionIntentOperation> residentOperations)
    {
        _store = store;
        _runs = runs;
        _residentOperations = residentOperations.ToArray();
    }

    public TransactionIntentLookup? LookupLatest(ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TransactionIntentLookup? latest = null;
        _store?.LookupLatest(_runs, key, ref latest);
        foreach (var operation in _residentOperations)
        {
            TransactionIntentLookup.Consider(ref latest, operation, key);
        }

        return latest;
    }

    public IEnumerator<byte[]> CreateKeyScan(
        byte[]? startInclusive,
        byte[]? endExclusive,
        PantsScanDirection direction)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var residentKeys = _residentOperations
            .Select(static operation => operation.Key)
            .Where(key =>
                (startInclusive is null || ByteArrayComparer.Instance.Compare(key, startInclusive) >= 0) &&
                (endExclusive is null || ByteArrayComparer.Instance.Compare(key, endExclusive) < 0))
            .Distinct(ByteArrayComparer.Instance)
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .Select(static key => key.ToArray())
            .ToList();
        if (direction == PantsScanDirection.Reverse)
        {
            residentKeys.Reverse();
        }

        var sources = new List<IEnumerator<byte[]>>(_runs.Length + 1)
        {
            residentKeys.GetEnumerator()
        };
        try
        {
            if (_store is not null)
            {
                foreach (var run in _runs)
                {
                    sources.Add(_store.CreateKeyCursor(
                        run,
                        startInclusive,
                        endExclusive,
                        direction));
                }
            }

            return new TransactionIntentKeyScan(sources, direction);
        }
        catch
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _store?.ReleaseReadView();
        }
    }
}
