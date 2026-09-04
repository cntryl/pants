namespace Cntryl.Pants.Storage.Internal;

interface ILocalWalStore
{
    WalCommitResult AppendCommit(
        CommitPayload payload,
        RuntimeState state,
        PantsDurability durability,
        WalMetricsRecorder? metrics = null);

    WalCommitGroupResult AppendCommitGroup(
        IReadOnlyList<WalCommitGroupEntry> commits,
        RuntimeState state,
        PantsDurability durability,
        Action beforeSync,
        WalMetricsRecorder? metrics = null);

    TimeSpan FlushDurabilityBoundary(WalMetricsRecorder? metrics = null);

    SealedWalSegment? SealActiveWalForCloud(
        WalMetricsRecorder? metrics = null,
        Action? validateCloudWriteAuthority = null);

    void CompleteCloudWalSeal(SealedWalSegment segment);

    ulong? RotateActiveLocalWal(WalMetricsRecorder? metrics = null);

    IReadOnlyList<SealedWalSegment> GetSealedWalSegmentsForCloudPublication();

    void DeleteCloudDurableWalSegment(SealedWalSegment segment);
}
