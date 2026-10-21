namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Bounds the V4 SST bytes needed to flush a set of WAL mutations without encoding the file.
/// </summary>
sealed class SstFlushFootprint
{
    const long DataEntryHeaderAndExtendedLengthBytes = 34;
    const long MaximumTrieNodeAndEdgeBytesPerBlock = 48;
    const long MaximumBloomBytesPerMutation = 21;
    const long SparseIndexEntryHeaderBytes = 20;
    const long ZstandardMaximumSmallBlockMargin = 64;
    const long Lz4MaximumBlockExpansionBytes = 20;
    const long BlockLengthAndChecksumTrailerBytes = 9;
    const long MaximumEncodedPayloadCopies = 4;
    const long ConstructionScratchBytesPerOperation = 192;
    const long ConservativePointEntryBytes = 256;
    const long ConservativeRangeEntryBytes = 128;
    const long ConservativeFixedSstBytes = 16 * 1024;

    long _operationCount;
    long _pointCount;
    long _pointKeyBytes;
    long _valueBytes;
    long _rangeCount;
    long _rangeBoundBytes;
    long _maximumBoundLength;
    bool _hasAnyKey;

    public void Add(
        int keyLength,
        int valueLength,
        int rangeEndLength,
        bool isRangeTombstone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        ArgumentOutOfRangeException.ThrowIfNegative(valueLength);
        ArgumentOutOfRangeException.ThrowIfNegative(rangeEndLength);
        _operationCount = checked(_operationCount + 1);
        _hasAnyKey = true;

        if (isRangeTombstone)
        {
            _rangeCount = checked(_rangeCount + 1);
            _rangeBoundBytes = checked(_rangeBoundBytes + keyLength + rangeEndLength);
            _maximumBoundLength = Math.Max(
                _maximumBoundLength,
                Math.Max(keyLength, rangeEndLength));
            return;
        }

        _pointCount = checked(_pointCount + 1);
        _pointKeyBytes = checked(_pointKeyBytes + keyLength);
        _valueBytes = checked(_valueBytes + valueLength);
        _maximumBoundLength = Math.Max(_maximumBoundLength, keyLength);
    }

    public long MeasureUpperBound(PantsPerformanceGoal performanceGoal)
    {
        if (_pointCount == 0 && _rangeCount == 0)
        {
            return 0;
        }

        // Each point entry stores at most its full key/value and the V4 entry header. A block can
        // hold several entries, but one entry may also force its own block, so point count bounds
        // both data-block count and the number of sparse-index/trie and bloom contributions.
        var dataRawBytes = checked(
            _pointKeyBytes +
            _valueBytes +
            _pointCount * DataEntryHeaderAndExtendedLengthBytes);
        var bloomRawBytes = checked(
            sizeof(uint) + _pointCount * MaximumBloomBytesPerMutation);
        var sparseIndexRawBytes = _pointCount == 0
            ? 0
            : checked(
                _pointKeyBytes +
                _pointCount * SparseIndexEntryHeaderBytes);
        var trieIndexRawBytes = _pointCount == 0
            ? 0
            : checked(_pointKeyBytes +
                       _pointCount * MaximumTrieNodeAndEdgeBytesPerBlock);
        var rangeRawBytes = _rangeCount == 0
            ? 0
            : checked(sizeof(uint) + _rangeBoundBytes + 20 * _rangeCount);
        var metadataRawBytes = checked(
            24 + (_hasAnyKey ? 8 + 2 * _maximumBoundLength : 0));
        var blockCount = checked(
            _pointCount +
            (_pointCount == 0 ? 3 : 4) +
            (_rangeCount == 0 ? 0 : 1));
        var rawBlockBytes = checked(
            dataRawBytes +
            bloomRawBytes +
            sparseIndexRawBytes +
            trieIndexRawBytes +
            rangeRawBytes +
            metadataRawBytes);
        var compressionOverhead = performanceGoal switch
        {
            PantsPerformanceGoal.Latency => checked(
                CeilingDivide(rawBlockBytes, 255) +
                blockCount * Lz4MaximumBlockExpansionBytes),
            PantsPerformanceGoal.Throughput => 0,
            PantsPerformanceGoal.Economy => checked(
                CeilingDivide(rawBlockBytes, 256) +
                blockCount * ZstandardMaximumSmallBlockMargin),
            _ => throw new PantsInternalException($"Unknown performance goal '{performanceGoal}'.")
        };

        return checked(
            rawBlockBytes +
            compressionOverhead +
            blockCount * BlockLengthAndChecksumTrailerBytes +
            DiskFormat.SstFooterSize);
    }

    /// <summary>
    ///     Bounds the hybrid cloud flush staging promised by one family before an indivisible
    ///     transaction is appended to the WAL.
    /// </summary>
    public long MeasureCloudFlushStagingUpperBound()
    {
        if (_pointCount == 0 && _rangeCount == 0)
        {
            return 0;
        }

        var conservativePointBytes = checked(
            _pointCount * ConservativePointEntryBytes +
            _pointKeyBytes * 8 +
            _valueBytes * 2);
        var conservativeRangeBytes = checked(
            _rangeCount * ConservativeRangeEntryBytes +
            _rangeBoundBytes * 6);
        var encodedUpperBound = checked(
            ConservativeFixedSstBytes +
            conservativePointBytes +
            conservativeRangeBytes);
        return checked(encodedUpperBound * 2);
    }

    public static long MeasureUpperBound(
        IEnumerable<WalMutation> operations,
        PantsPerformanceGoal performanceGoal)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var footprint = new SstFlushFootprint();
        foreach (var operation in operations)
        {
            footprint.Add(
                operation.Key.Length,
                operation.Value?.Length ?? 0,
                operation.RangeEnd?.Length ?? 0,
                operation.Operation == WalOperation.DeleteRange);
        }

        return footprint.MeasureUpperBound(performanceGoal);
    }

    /// <summary>
    ///     A conservative bound for flush construction scratch: four payload copies plus fixed
    ///     per-operation accounting for grouping, entries, sorting, indexes, and range metadata.
    /// </summary>
    public static long MeasureWorkingSetUpperBound(
        IEnumerable<WalMutation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var footprint = new SstFlushFootprint();
        foreach (var operation in operations)
        {
            footprint.Add(
                operation.Key.Length,
                operation.Value?.Length ?? 0,
                operation.RangeEnd?.Length ?? 0,
                operation.Operation == WalOperation.DeleteRange);
        }

        var sourceBytes = checked(
            footprint._pointKeyBytes +
            footprint._valueBytes +
            footprint._rangeBoundBytes);
        return checked(
            MaximumEncodedPayloadCopies * sourceBytes +
            ConstructionScratchBytesPerOperation * footprint._operationCount);
    }

    static long CeilingDivide(long value, long divisor) => value == 0
        ? 0
        : checked((value - 1) / divisor + 1);
}
