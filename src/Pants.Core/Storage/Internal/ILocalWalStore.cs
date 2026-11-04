namespace Cntryl.Pants.Storage.Internal;

interface ILocalWalStore
{
    /// <summary>
    ///     Whether an ambiguous append or sync failure has fenced the writer until restart recovery.
    /// </summary>
    bool IsWalFenced { get; }

    WalCommitResult AppendCommit(
        CommitPayload payload,
        ulong publishedSequence,
        PantsDurability durability,
        WalMetricsRecorder? metrics = null);

    WalCommitGroupResult AppendCommitGroup(
        IReadOnlyList<WalCommitGroupEntry> commits,
        ulong publishedSequence,
        PantsDurability durability,
        Action beforeSync,
        WalMetricsRecorder? metrics = null);

    TimeSpan FlushDurabilityBoundary(WalMetricsRecorder? metrics = null);

    SealedWalSegment? SealActiveWalForCloud(
        WalMetricsRecorder? metrics = null,
        Action? validateCloudWriteAuthority = null);

    void CompleteCloudWalSeal(SealedWalSegment segment);

    ulong? RotateActiveLocalWal(WalMetricsRecorder? metrics = null);

    SealedWalBacklogBatch GetSealedWalSegmentsForCloudPublication(
        ulong? throughSegmentId = null,
        int maximumSegments = SealedWalBacklogBatch.DefaultMaximumSegments,
        long maximumBytes = SealedWalBacklogBatch.DefaultMaximumBytes);

    void DeleteCloudDurableWalSegment(SealedWalSegment segment);
}
