using System.Collections;

namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionSpillRunKeyCursor : IEnumerator<byte[]>
{
    readonly ulong _dataEnd;
    readonly byte[]? _endExclusive;
    readonly bool _reverse;
    readonly ulong[] _reverseFrameOffsets;
    readonly IReadOnlyList<ulong> _reverseOffsets;
    readonly TransactionSpillRun _run;
    readonly byte[]? _startInclusive;
    readonly TransactionSpillStore _store;
    ulong _cursor;
    int _disposed;
    bool _exhausted;
    int _nextReverseChunk;
    int _nextReverseFrame;
    byte[]? _previousKey;

    internal TransactionSpillRunKeyCursor(
        TransactionSpillStore store,
        TransactionSpillRun run,
        byte[]? startInclusive,
        byte[]? endExclusive,
        PantsScanDirection direction)
    {
        _store = store;
        _run = run;
        _startInclusive = startInclusive?.ToArray();
        _endExclusive = endExclusive?.ToArray();
        _reverse = direction == PantsScanDirection.Reverse;
        try
        {
            using var stream = store.OpenRunReadStream(run);
            var header = store.ReadRunHeader(stream, run);
            _dataEnd = header.OrdinalTableOffset;
            _exhausted = header.RecordCount == 0;
            if (_reverse)
            {
                _reverseOffsets = TransactionSpillStore.ReadSparseOffsets(stream, header);
                _reverseFrameOffsets = new ulong[Math.Min(
                    TransactionSpillStore.SparseIndexStride,
                    run.RecordCount)];
                _nextReverseChunk = _reverseOffsets.Count;
                _cursor = TransactionSpillStore.RunHeaderLength;
            }
            else
            {
                _reverseOffsets = [];
                _reverseFrameOffsets = [];
                _cursor = TransactionSpillStore.FindSparseStart(
                    stream,
                    header,
                    _startInclusive);
            }
        }
        catch (Exception exception)
        {
            throw TransactionSpillStore.MapReadException(exception);
        }
    }

    public byte[] Current { get; private set; } = [];

    object IEnumerator.Current => Current;

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

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    byte[]? NextForwardKey()
    {
        using var stream = _store.OpenRunReadStream(_run);
        while (_cursor < _dataEnd)
        {
            stream.Position = checked((long)_cursor);
            var (_, key, _) = _store.ReadOperationPrimaryKeyFrame(stream);
            var nextCursor = checked((ulong)stream.Position);
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
            if (_nextReverseFrame != 0)
            {
                var offset = _reverseFrameOffsets[--_nextReverseFrame];
                using var stream = _store.OpenRunReadStream(_run);
                stream.Position = checked((long)offset);
                var (_, key, _) = _store.ReadOperationPrimaryKeyFrame(stream);
                if (!IsInBounds(key) ||
                    (_previousKey is not null &&
                     ByteArrayComparer.Instance.Equals(_previousKey, key)))
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

        var chunkIndex = --_nextReverseChunk;
        var chunkStart = _reverseOffsets[chunkIndex];
        var chunkEnd = chunkIndex + 1 < _reverseOffsets.Count
            ? _reverseOffsets[chunkIndex + 1]
            : _dataEnd;
        var cursor = chunkStart;
        var frameCount = 0;
        using (var stream = _store.OpenRunReadStream(_run))
        {
            while (cursor < chunkEnd)
            {
                if (frameCount == _reverseFrameOffsets.Length)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill sparse chunk exceeds its operation stride.");
                }

                _reverseFrameOffsets[frameCount++] = cursor;
                stream.Position = checked((long)cursor);
                _ = _store.ReadOperationPrimaryKeyFrame(stream);
                var nextCursor = checked((ulong)stream.Position);
                if (nextCursor > chunkEnd || nextCursor <= cursor)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill sparse chunk does not align to operation frames.");
                }

                cursor = nextCursor;
            }
        }

        var expectedFrameCount = Math.Min(
            TransactionSpillStore.SparseIndexStride,
            _run.RecordCount - (chunkIndex * TransactionSpillStore.SparseIndexStride));
        if (cursor != chunkEnd || frameCount != expectedFrameCount)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse chunk has an invalid operation count.");
        }

        _nextReverseFrame = frameCount;
        return true;
    }

    bool IsInBounds(byte[] key) =>
        (_startInclusive is null || ByteArrayComparer.Instance.Compare(key, _startInclusive) >= 0) &&
        (_endExclusive is null || ByteArrayComparer.Instance.Compare(key, _endExclusive) < 0);
}
