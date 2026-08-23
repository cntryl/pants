namespace Cntryl.Pants;

internal readonly record struct SstReadSample
{
    public int SstsTouched { get; init; }

    public int L0SstsTouched { get; init; }

    public int AmplificationBlocksRead { get; init; }

    public int DataBlocksRead { get; init; }

    public int ReaderCacheHits { get; init; }

    public int ReaderCacheMisses { get; init; }

    public int BlockCacheHits { get; init; }

    public int BlockCacheMisses { get; init; }

    public int CandidateBlocks { get; init; }

    public int KeyRangeRejects { get; init; }

    public int BloomChecks { get; init; }

    public int BloomTruePositives { get; init; }

    public int BloomFalsePositives { get; init; }

    public int BloomTrueNegatives { get; init; }

    public int RangeTombstoneScans { get; init; }

    public bool ExceedsBudget =>
        SstsTouched > ReadAmplificationBudget.MaximumSstsPerRead ||
        AmplificationBlocksRead > ReadAmplificationBudget.MaximumBlocksPerRead;
}
