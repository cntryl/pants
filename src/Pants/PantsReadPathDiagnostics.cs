namespace Pants;

public sealed record PantsReadPathDiagnostics
{
    public long ReadOnlyTransactionsBegun { get; init; }

    public long ReadOnlySnapshotCacheHits { get; init; }

    public long ReadOnlySnapshotCacheMisses { get; init; }

    public long SnapshotsRegistered { get; init; }

    public long SnapshotsUnregistered { get; init; }

    public long SstReaderCacheHits { get; init; }

    public long SstReaderCacheMisses { get; init; }

    public long SstBlockCacheHits { get; init; }

    public long SstBlockCacheMisses { get; init; }

    public long CandidateSstFilesChecked { get; init; }

    public long CandidateBlocksChecked { get; init; }

    public long DataBlocksRead { get; init; }

    public long BloomChecks { get; init; }

    public long BloomRejects { get; init; }

    public long RangeTombstoneScans { get; init; }
}
