namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionIntentReadView : IDisposable
{
    static readonly IComparer<TransactionIntentOperation> ResidentOperationComparer =
        Comparer<TransactionIntentOperation>.Create(static (left, right) =>
        {
            var keyOrder = ByteArrayComparer.Instance.Compare(left.Key, right.Key);
            return keyOrder != 0 ? keyOrder : left.Ordinal.CompareTo(right.Ordinal);
        });

    readonly TransactionIntentOperation[] _residentOperations;
    readonly TransactionSpillRun[] _runs;
    readonly TransactionSpillStore? _store;
    readonly TransactionMemoryPool _memoryPool;
    readonly long _reservedBytes;
    int _disposed;

    internal TransactionIntentReadView(
        TransactionSpillStore? store,
        IReadOnlyList<TransactionSpillRun> runs,
        IReadOnlyList<TransactionIntentOperation> residentOperations,
        TransactionMemoryPool memoryPool)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(residentOperations);
        _memoryPool = memoryPool ?? throw new ArgumentNullException(nameof(memoryPool));
        _reservedBytes = SnapshotBytes(residentOperations.Count, runs.Count);
        if (_reservedBytes != 0 && !_memoryPool.TryReserve(_reservedBytes))
        {
            throw PantsException.ResourceLimit(
                "The transaction memory pool cannot admit a transaction read snapshot.");
        }

        _store = store;
        try
        {
            _runs = runs.ToArray();
            _residentOperations = residentOperations.ToArray();
            Array.Sort(_residentOperations, ResidentOperationComparer);
        }
        catch
        {
            if (_reservedBytes != 0)
            {
                _memoryPool.Release(_reservedBytes);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _store?.ReleaseReadView();
            }
            finally
            {
                if (_reservedBytes != 0)
                {
                    _memoryPool.Release(_reservedBytes);
                }
            }
        }
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
        var reservedBytes = KeyScanBytes(_runs, direction);
        if (reservedBytes != 0 && !_memoryPool.TryReserve(reservedBytes))
        {
            throw PantsException.ResourceLimit(
                $"The transaction memory pool cannot admit {reservedBytes} bytes for spill scan " +
                $"readers and index state; it currently uses {_memoryPool.Used} bytes.");
        }

        List<IEnumerator<byte[]>>? sources = null;
        try
        {
            sources = [new TransactionIntentResidentKeyCursor(
                _residentOperations,
                startInclusive,
                endExclusive,
                direction)];
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

            return new TransactionIntentKeyScan(
                sources,
                direction,
                _memoryPool,
                reservedBytes);
        }
        catch
        {
            try
            {
                if (sources is not null)
                {
                    foreach (var source in sources)
                    {
                        source.Dispose();
                    }
                }
            }
            finally
            {
                if (reservedBytes != 0)
                {
                    _memoryPool.Release(reservedBytes);
                }
            }

            throw;
        }
    }

    static long KeyScanBytes(
        TransactionSpillRun[] runs,
        PantsScanDirection direction)
    {
        const long listObjectBytes = 32;
        if (direction != PantsScanDirection.Reverse)
        {
            return 0;
        }

        long bytes = 0;
        foreach (var run in runs)
        {
            var sparseCount = checked((int)(
                ((long)run.RecordCount + TransactionSpillStore.SparseIndexStride - 1) /
                TransactionSpillStore.SparseIndexStride));
            bytes = checked(
                bytes +
                SparseOffsetArrayBytes(sparseCount) +
                listObjectBytes +
                SparseOffsetArrayBytes(Math.Min(
                    TransactionSpillStore.SparseIndexStride,
                    run.RecordCount)));
        }

        return bytes;
    }

    static long SnapshotBytes(int residentCount, int runCount) =>
        checked(ArrayBytes(residentCount) + ArrayBytes(runCount));

    // Four pointer-sized slots cover the runtime array header and alignment slack.
    static long ArrayBytes(int count) => count == 0
        ? 0
        : checked((long)IntPtr.Size * (count + 4L));

    static long SparseOffsetArrayBytes(int count) => count == 0
        ? 0
        : checked((sizeof(ulong) * (long)count) + (IntPtr.Size * 4L));

}
