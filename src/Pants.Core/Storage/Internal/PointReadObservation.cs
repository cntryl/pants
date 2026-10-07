namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Collects read-amplification telemetry and diagnostics while a point read resolves its value,
///     so the read pass that finds the value is the only pass over the SSTs.
/// </summary>
sealed class PointReadObservation(bool captureTraces)
{
    readonly List<PantsSstReadTrace>? _traces = captureTraces ? [] : null;
    int _amplificationBlocksRead;
    int _bloomChecks;
    int _bloomFalsePositives;
    int _bloomTrueNegatives;
    int _bloomTruePositives;
    int _blockCacheHits;
    int _blockCacheMisses;
    int _candidateBlocks;
    int _dataBlocksRead;
    int _l0SstsTouched;
    int _readerCacheHits;
    int _readerCacheMisses;
    int _sstsTouched;

    /// <summary>Whether the read reached the SST stage, even if no file was a candidate.</summary>
    public bool ReachedSsts { get; private set; }

    public int KeyRangeRejects { get; private set; }

    public PantsPointReadTrace Trace => new(KeyRangeRejects, [.. _traces ?? []]);

    public void BeginSstStage(int familyFileCount, int candidateCount)
    {
        ReachedSsts = true;
        KeyRangeRejects = Math.Max(0, familyFileCount - candidateCount);
    }

    /// <summary>Records one candidate file once the read has finished with it.</summary>
    /// <param name="blockCacheHit">Whether the candidate block came from the block cache.</param>
    /// <param name="candidateBlockContainsKey">
    ///     Whether the candidate block held the key; ignored when the bloom filter rejected the file.
    /// </param>
    public void RecordCandidate(
        FileMeta file,
        SstPointReadDecision decision,
        PantsSstReadTier tier,
        bool readerCacheHit,
        bool blockCacheHit,
        bool candidateBlockContainsKey)
    {
        _sstsTouched++;
        if (file.Level == 0)
        {
            _l0SstsTouched++;
        }

        if (readerCacheHit)
        {
            _readerCacheHits++;
        }
        else
        {
            _readerCacheMisses++;
        }

        _bloomChecks = checked(_bloomChecks + decision.BloomChecks);
        _candidateBlocks = checked(_candidateBlocks + decision.CandidateBlocks);
        _bloomTrueNegatives = checked(_bloomTrueNegatives + (decision.Rejected ? 1 : 0));
        _amplificationBlocksRead = checked(_amplificationBlocksRead + 1 + decision.BlocksRead);
        var blockOutcome = PantsCacheReadOutcome.NotChecked;
        var bloomOutcome = decision.Rejected
            ? PantsBloomFilterOutcome.Rejected
            : PantsBloomFilterOutcome.NotChecked;
        var dataBlocksRead = 0;
        if (decision.BlocksRead != 0)
        {
            if (blockCacheHit)
            {
                _blockCacheHits++;
                blockOutcome = PantsCacheReadOutcome.Hit;
            }
            else
            {
                _blockCacheMisses++;
                _dataBlocksRead = checked(_dataBlocksRead + 1);
                dataBlocksRead = 1;
                blockOutcome = PantsCacheReadOutcome.Miss;
            }

            if (candidateBlockContainsKey)
            {
                _bloomTruePositives++;
                bloomOutcome = PantsBloomFilterOutcome.TruePositive;
            }
            else
            {
                _bloomFalsePositives++;
                bloomOutcome = PantsBloomFilterOutcome.FalsePositive;
            }
        }

        _traces?.Add(new PantsSstReadTrace(
            file.Name,
            file.Level,
            tier,
            bloomOutcome,
            readerCacheHit ? PantsCacheReadOutcome.Hit : PantsCacheReadOutcome.Miss,
            blockOutcome,
            dataBlocksRead));
    }

    public SstReadSample ToSample() => new()
    {
        SstsTouched = _sstsTouched,
        L0SstsTouched = _l0SstsTouched,
        AmplificationBlocksRead = _amplificationBlocksRead,
        DataBlocksRead = _dataBlocksRead,
        ReaderCacheHits = _readerCacheHits,
        ReaderCacheMisses = _readerCacheMisses,
        BlockCacheHits = _blockCacheHits,
        BlockCacheMisses = _blockCacheMisses,
        CandidateBlocks = _candidateBlocks,
        KeyRangeRejects = KeyRangeRejects,
        BloomChecks = _bloomChecks,
        BloomTruePositives = _bloomTruePositives,
        BloomFalsePositives = _bloomFalsePositives,
        BloomTrueNegatives = _bloomTrueNegatives
    };
}
