namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstBlockIterator : IAsyncDisposable
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly AsyncSstReader _reader;
    readonly ResourceBudget? _resourceBudget;
    readonly byte[]? _startInclusive;
    int _blockIndex;
    IReadOnlyList<SstEntry>? _blockEntries;
    IDisposable? _blockReservation;
    bool _finished;
    int _positionInBlock;
    bool _started;

    public AsyncSstBlockIterator(
        AsyncSstReader reader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget)
    {
        _reader = reader;
        _direction = direction;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _resourceBudget = resourceBudget;
    }

    public SstEntry Current { get; private set; } = null!;

    public int DataBlocksRead { get; private set; }

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
                _finished = true;
                return false;
            }

            await LoadBlockAsync(_blockIndex, cancellationToken).ConfigureAwait(false);
        }

        return _direction == PantsScanDirection.Forward
            ? await AdvanceForwardAsync(cancellationToken).ConfigureAwait(false)
            : await AdvanceReverseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _blockEntries = null;
        _blockReservation?.Dispose();
        _blockReservation = null;
        await _reader.DisposeAsync().ConfigureAwait(false);
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
                    _finished = true;
                    return false;
                }

                Current = candidate;
                return true;
            }

            _blockIndex++;
            if (_blockIndex >= _reader.DataBlockCount)
            {
                _finished = true;
                return false;
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
                    _finished = true;
                    return false;
                }

                Current = candidate;
                return true;
            }

            _blockIndex--;
            if (_blockIndex < 0)
            {
                _finished = true;
                return false;
            }

            await LoadBlockAsync(_blockIndex, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask LoadBlockAsync(int blockIndex, CancellationToken cancellationToken)
    {
        _blockEntries = null;
        _blockReservation?.Dispose();
        _blockReservation = null;
        var block = await _reader.ReadDataBlockAsync(blockIndex, cancellationToken)
            .ConfigureAwait(false);
        DataBlocksRead = checked(DataBlocksRead + 1);
        using var transientReservation = _resourceBudget?.Reserve(block.Length);
        var entries = SstCodec.DecodeDataBlock(block);
        var accountedBytes = checked(entries.Sum(static entry =>
            entry.Key.Length + (entry.Value?.Length ?? 0) + 32));
        _blockReservation = _resourceBudget?.Reserve(accountedBytes);
        _blockEntries = entries;
        _positionInBlock = _direction == PantsScanDirection.Forward ? 0 : entries.Count - 1;
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
