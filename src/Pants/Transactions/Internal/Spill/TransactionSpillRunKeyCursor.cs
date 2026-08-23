namespace Cntryl.Pants;

internal sealed class TransactionSpillRunKeyCursor : IEnumerator<byte[]>
{
    readonly TransactionSpillStore _store;
    readonly FileStream _stream;
    readonly ulong _dataEnd;
    readonly byte[]? _startInclusive;
    readonly byte[]? _endExclusive;
    readonly bool _reverse;
    readonly List<(ulong Start, ulong End)> _reverseChunks;
    IEnumerator<byte[]> _reverseKeys = Enumerable.Empty<byte[]>().GetEnumerator();
    byte[]? _previousKey;
    ulong _cursor;
    int _nextReverseChunk;
    bool _exhausted;
    int _disposed;

    internal TransactionSpillRunKeyCursor(
        TransactionSpillStore store,
        TransactionSpillRun run,
        byte[]? startInclusive,
        byte[]? endExclusive,
        PantsScanDirection direction)
    {
        _store = store;
        _startInclusive = startInclusive?.ToArray();
        _endExclusive = endExclusive?.ToArray();
        _reverse = direction == PantsScanDirection.Reverse;
        try
        {
            _stream = new FileStream(
                run.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1_024,
                FileOptions.RandomAccess);
            var header = store.ReadRunHeader(_stream, run);
            _dataEnd = header.OrdinalTableOffset;
            _exhausted = header.RecordCount == 0;
            if (_reverse)
            {
                var starts = TransactionSpillStore.ReadSparseOffsets(_stream, header);
                var chunks = new List<(ulong Start, ulong End)>(starts.Count);
                for (var index = 0; index < starts.Count; index++)
                {
                    chunks.Add((
                        starts[index],
                        index + 1 < starts.Count ? starts[index + 1] : header.OrdinalTableOffset));
                }

                _reverseChunks = chunks;
                _nextReverseChunk = chunks.Count;
                _cursor = TransactionSpillStore.RunHeaderLength;
            }
            else
            {
                _reverseChunks = [];
                _cursor = TransactionSpillStore.FindSparseStart(_stream, header, _startInclusive);
            }
        }
        catch (Exception exception)
        {
            _stream?.Dispose();
            throw TransactionSpillStore.MapReadException(exception);
        }
    }

    public byte[] Current { get; private set; } = [];

    object System.Collections.IEnumerator.Current => Current;

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_exhausted)
        {
            return false;
        }

        try
        {
            var key = _reverse ? NextReverseKey() : NextForwardKey();
            if (key is null)
            {
                _exhausted = true;
                Current = [];
                return false;
            }

            Current = key;
            return true;
        }
        catch (Exception exception)
        {
            _exhausted = true;
            throw TransactionSpillStore.MapReadException(exception);
        }
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _reverseKeys.Dispose();
            _stream.Dispose();
        }
    }

    byte[]? NextForwardKey()
    {
        while (_cursor < _dataEnd)
        {
            _stream.Position = checked((long)_cursor);
            var (_, key, _) = _store.ReadOperationPrimaryKeyFrame(_stream);
            var nextCursor = checked((ulong)_stream.Position);
            if (nextCursor > _dataEnd || nextCursor <= _cursor)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill data frame exceeds its data section.");
            }

            _cursor = nextCursor;
            if (_endExclusive is not null &&
                ByteArrayComparer.Instance.Compare(key, _endExclusive) >= 0)
            {
                return null;
            }

            if (!IsInBounds(key) ||
                (_previousKey is not null &&
                 ByteArrayComparer.Instance.Equals(_previousKey, key)))
            {
                continue;
            }

            _previousKey = key;
            return key;
        }

        return null;
    }

    byte[]? NextReverseKey()
    {
        while (true)
        {
            if (_reverseKeys.MoveNext())
            {
                var key = _reverseKeys.Current;
                if (_previousKey is not null && ByteArrayComparer.Instance.Equals(_previousKey, key))
                {
                    continue;
                }

                _previousKey = key;
                return key;
            }

            if (!LoadReverseChunk())
            {
                return null;
            }
        }
    }

    bool LoadReverseChunk()
    {
        if (_nextReverseChunk == 0)
        {
            return false;
        }

        _nextReverseChunk--;
        var chunk = _reverseChunks[_nextReverseChunk];
        var cursor = chunk.Start;
        var keys = new List<byte[]>();
        while (cursor < chunk.End)
        {
            _stream.Position = checked((long)cursor);
            var (_, key, _) = _store.ReadOperationPrimaryKeyFrame(_stream);
            var nextCursor = checked((ulong)_stream.Position);
            if (nextCursor > chunk.End || nextCursor <= cursor)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill sparse chunk does not align to operation frames.");
            }

            cursor = nextCursor;
            if (IsInBounds(key) &&
                (keys.Count == 0 || !ByteArrayComparer.Instance.Equals(keys[^1], key)))
            {
                keys.Add(key);
            }
        }

        keys.Reverse();
        _reverseKeys.Dispose();
        _reverseKeys = keys.GetEnumerator();
        return true;
    }

    bool IsInBounds(byte[] key) =>
        (_startInclusive is null || ByteArrayComparer.Instance.Compare(key, _startInclusive) >= 0) &&
        (_endExclusive is null || ByteArrayComparer.Instance.Compare(key, _endExclusive) < 0);
}
