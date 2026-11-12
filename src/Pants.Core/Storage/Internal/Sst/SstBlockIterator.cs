namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Lazily walks the data blocks of an open <see cref="SstReader" /> one block at a time,
///     forward or reverse, decoding at most one block's entries at any moment. Used by scans
///     (<see cref="PantsScanDirection" />) and compaction so neither has to fully decode an SST
///     up front the way <see cref="SstCodec.Decode" /> does. When supplied, a shared
///     <see cref="ResourceBudget" /> accounts for the current decoded block and releases it as the
///     cursor advances or is disposed.
/// </summary>
sealed class SstBlockIterator : IDisposable
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly SstReader _reader;
    readonly ResourceBudget? _resourceBudget;
    readonly byte[]? _startInclusive;
    IReadOnlyList<SstEntry>? _blockEntries;
    SstEntry? _boundaryEntry;
    int _blockIndex;
    IDisposable? _blockReservation;
    bool _finished;
    int _positionInBlock;
    bool _started;

    SstBlockIterator(
        SstReader reader,
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

    /// <summary>Current entry. Valid only after <see cref="MoveNext" /> returns <c>true</c>.</summary>
    public SstEntry Current { get; private set; } = null!;

    /// <summary>Byte size of the data block <see cref="Current" /> was decoded from.</summary>
    public int CurrentBlockBytes { get; private set; }

    public void Dispose() => _ = Finish();

    public static SstBlockIterator Create(
        SstReader reader,
        PantsScanDirection direction,
        byte[]? startInclusive = null,
        byte[]? endExclusive = null,
        ResourceBudget? resourceBudget = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        return new SstBlockIterator(
            reader,
            direction,
            startInclusive,
            endExclusive,
            resourceBudget);
    }

    public bool MoveNext()
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

            LoadBlock(_blockIndex);
        }

        return _direction == PantsScanDirection.Forward ? AdvanceForward() : AdvanceReverse();
    }

    bool AdvanceForward()
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

            LoadBlock(_blockIndex);
        }
    }

    bool AdvanceReverse()
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

            LoadBlock(_blockIndex);
        }
    }

    void LoadBlock(int blockIndex)
    {
        _blockEntries = null;
        _blockReservation?.Dispose();
        _blockReservation = null;
        using var boundaryReservation = _boundaryEntry is null
            ? null
            : _resourceBudget?.Reserve(checked(
                _boundaryEntry.Key.Length + (_boundaryEntry.Value?.Length ?? 0) + SstCodec.DecodedEntryOverheadBytes));
        using var ownedBlock = _reader.ReadDataBlockOwned(blockIndex, _resourceBudget);
        var block = ownedBlock.Content;
        IDisposable? decodedReservation = _resourceBudget?.Reserve(SstCodec.MeasureDecodedDataBlockBytes(block));
        try
        {
            var entries = SstCodec.DecodeDataBlock(block);
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
            CurrentBlockBytes = block.Length;
            _blockEntries = entries;
            _positionInBlock = _direction == PantsScanDirection.Forward ? 0 : _blockEntries.Count - 1;
        }
        finally
        {
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
        CurrentBlockBytes = 0;
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

        var floor = FindFloorBlock(_endExclusive);
        blockIndex = floor;
        return floor >= 0;
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
