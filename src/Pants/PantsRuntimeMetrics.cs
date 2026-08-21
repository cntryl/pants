namespace Pants;

public sealed record PantsRuntimeMetrics
{
    public PantsEngineHealth Health { get; init; } = PantsEngineHealth.Healthy;
    public long CurrentSequence { get; init; }
    public long ManifestLastPersistedSequence { get; init; }
    public long ManifestNextWalSequence { get; init; }
    public int ActiveMemtables { get; init; }
    public int ImmutableMemtables { get; init; }
    public long TotalMemtableBytes { get; init; }
    public long MemtableSizeLimitBytes { get; init; }
    public long MemtableFlushThresholdBytes { get; init; }
    public long MaximumMemtableWalSegmentGap { get; init; }
    public bool WriteStalled { get; init; }
    public long WalCurrentSegmentId { get; init; }
    public int WalPendingWrites { get; init; }
    public long WalLastSyncedSequence { get; init; }
    public long WalLocalDurableSequence { get; init; }
    public long WalCloudDurableSequence { get; init; }
    public int PendingCompactions { get; init; }
    public int CompactingSsts { get; init; }
    public int ActiveCompactions { get; init; }
    public int PendingCloudUploads { get; init; }
    public int ActiveSnapshots { get; init; }
    public int PinnedSsts { get; init; }
    public long OldestSnapshotAgeSeconds { get; init; }
    public int SstCount { get; init; }
    public long SstBytes { get; init; }
    public long SalvageModeOpens { get; init; }
    public long NoSpaceEvents { get; init; }
    public long CompactionsRun { get; init; }
    public long CompactionBytesRewritten { get; init; }
    public long CompactionFailures { get; init; }
    public int ObsoleteFileBacklog { get; init; }
    public long WriteStallsTotal { get; init; }
    public long WriteConflictsTotal { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public long WalAppendCount { get; init; }
    public long WalFlushCount { get; init; }
    public long WalFsyncCount { get; init; }
    public long SstBloomRejectsTotal { get; init; }
    public long SstBloomChecksTotal { get; init; }
    public long SstDataBlocksReadTotal { get; init; }
    public int FlushQueueDepth { get; init; }
    public int FlushInFlight { get; init; }
    public long FlushEnqueuedTotal { get; init; }
    public long FlushFailuresTotal { get; init; }
    public long FlushRetriesTotal { get; init; }
    public long PendingEvictions { get; init; }
    public long WalRecoveryRecordsReplayed { get; init; }
    public long WalRecoveryBytesReplayed { get; init; }
    public long IntentLogReplayRuns { get; init; }
    public long IntentLogEntriesReplayed { get; init; }
}
