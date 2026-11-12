namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstBlockIterator : IAsyncDisposable
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly AsyncSstReader _reader;
    readonly ResourceBudget? _resourceBudget;
    readonly SstScanBlockCache? _blockCache;
    readonly byte[]? _startInclusive;
    IReadOnlyList<SstEntry>? _blockEntries;
    SstEntry? _boundaryEntry;
    int _blockIndex;
    IDisposable? _blockReservation;
    bool _finished;
    int _positionInBlock;
    bool _started;

    public AsyncSstBlockIterator(
        AsyncSstReader reader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget,
        SstScanBlockCache? blockCache = null)
    {
        _reader = reader;
        _direction = direction;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _resourceBudget = resourceBudget;
        _blockCache = blockCache;
    }

    public SstEntry Current { get; private set; } = null!;

    public int DataBlocksRead { get; private set; }

    public int BlockCacheHits { get; private set; }

    public int BlockCacheMisses { get; private set; }

    public async ValueTask DisposeAsync()
    {
        _ = Finish();
        await _reader.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return false;
        }

        if (!_started)
        {
            _started = true;
            if (_reader.DataBlockCount == 0 || !TryResolveStartBlock(out _blockIndex))
            {
                return Finish();
            }

            await LoadBlockAsync(_blockIndex, cancellationToken).ConfigureAwait(false);
        }

        return _direction == PantsScanDirection.Forward
            ? await AdvanceForwardAsync(cancellationToken).ConfigureAwait(false)
            : await AdvanceReverseAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<bool> AdvanceForwardAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_blockEntries is not null && _positionInBlock < _blockEntries.Count)
            {
                var candidate = _blockEntries[_positionInBlock++];
                if (_startInclusive is not null &&
                    candidate.Key.AsSpan().SequenceCompareTo(_startInclusive) < 0)
                {
                    continue;
                }

                if (_endExclusive is not null &&
                    candidate.Key.AsSpan().SequenceCompareTo(_endExclusive) >= 0)
                {
                    return Finish();
                }

                Current = candidate;
                return true;
            }

            _blockIndex++;
            if (_blockIndex >= _reader.DataBlockCount)
            {
                return Finish();
            }

            await LoadBlockAsync(_blockIndex, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask<bool> AdvanceReverseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_blockEntries is not null && _positionInBlock >= 0)
            {
                var candidate = _blockEntries[_positionInBlock--];
                if (_endExclusive is not null &&
                    candidate.Key.AsSpan().SequenceCompareTo(_endExclusive) >= 0)
                {
                    continue;
                }

                if (_startInclusive is not null &&
                    candidate.Key.AsSpan().SequenceCompareTo(_startInclusive) < 0)
                {
                    return Finish();
                }

                Current = candidate;
                return true;
            }

            _blockIndex--;
            if (_blockIndex < 0)
            {
                return Finish();
            }

            await LoadBlockAsync(_blockIndex, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask LoadBlockAsync(int blockIndex, CancellationToken cancellationToken)
    {
        _blockEntries = null;
        _blockReservation?.Dispose();
        _blockReservation = null;
        using var boundaryReservation = _boundaryEntry is null
            ? null
            : _resourceBudget?.Reserve(checked(
                _boundaryEntry.Key.Length + (_boundaryEntry.Value?.Length ?? 0) + SstCodec.DecodedEntryOverheadBytes));
        SstReadBlock? ownedBlock = null;
        ReadOnlyMemory<byte> block = default;
        IDisposable? decodedReservation = null;
        try
        {
            var cacheHit = _blockCache is not null && _blockCache.TryGet(blockIndex, out block);
            if (cacheHit)
            {
                BlockCacheHits = checked(BlockCacheHits + 1);
            }
            else
            {
                if (_blockCache is not null)
                {
                    BlockCacheMisses = checked(BlockCacheMisses + 1);
                }

                ownedBlock = await _reader.ReadDataBlockOwnedAsync(blockIndex, _resourceBudget, cancellationToken)
                    .ConfigureAwait(false);
                block = ownedBlock.Content;
                DataBlocksRead = checked(DataBlocksRead + 1);
            }
            decodedReservation = _resourceBudget?.Reserve(SstCodec.MeasureDecodedDataBlockBytes(block.Span));
            var entries = SstCodec.DecodeDataBlock(block.Span);
            if (entries.Count == 0 || !entries[0].Key.AsSpan().SequenceEqual(_reader.GetFirstKey(blockIndex)))
            {
                throw new PantsCorruptionException("SST index first key does not match its data block.");
            }

            if (_boundaryEntry is not null)
            {
                if (_direction == PantsScanDirection.Forward)
                {
                    SstCodec.ValidateAdjacentEntries(_boundaryEntry, entries[0]);
                }
                else
                {
                    SstCodec.ValidateAdjacentEntries(entries[^1], _boundaryEntry);
                }
            }

            _boundaryEntry = _direction == PantsScanDirection.Forward ? entries[^1] : entries[0];
            _blockReservation = decodedReservation;
            decodedReservation = null;
            if (!cacheHit)
            {
                // Admit only complete, validated blocks after their retained decode fits. The
                // shared cache never receives an invalid or partially admitted scan block.
                _blockCache?.Admit(blockIndex, block.Span);
            }
            _blockEntries = entries;
            _positionInBlock = _direction == PantsScanDirection.Forward ? 0 : entries.Count - 1;
        }
        finally
        {
            ownedBlock?.Dispose();
            decodedReservation?.Dispose();
        }
    }

    bool Finish()
    {
        _finished = true;
        _blockEntries = null;
        _boundaryEntry = null;
        Current = null!;
        _blockReservation?.Dispose();
        _blockReservation = null;
        return false;
    }

    bool TryResolveStartBlock(out int blockIndex)
    {
        if (_direction == PantsScanDirection.Forward)
        {
            blockIndex = _startInclusive is null ? 0 : Math.Max(0, FindFloorBlock(_startInclusive));
            while (blockIndex > 0 &&
                   _reader.GetFirstKey(blockIndex).AsSpan().SequenceEqual(_startInclusive))
            {
                blockIndex--;
            }

            return true;
        }

        if (_endExclusive is null)
        {
            blockIndex = _reader.DataBlockCount - 1;
            return true;
        }

        blockIndex = FindFloorBlock(_endExclusive);
        return blockIndex >= 0;
    }

    int FindFloorBlock(byte[] key)
    {
        var low = 0;
        var high = _reader.DataBlockCount - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (key.AsSpan().SequenceCompareTo(_reader.GetFirstKey(middle)) < 0)
            {
                high = middle - 1;
            }
            else
            {
                result = middle;
                low = middle + 1;
            }
        }

        return result;
    }
}
