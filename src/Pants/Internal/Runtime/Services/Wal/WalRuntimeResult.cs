namespace Pants;

readonly record struct WalRuntimeResult(
    SealedWalSegment? SealedSegment = null,
    ulong? LocalMaximumSequence = null,
    IReadOnlyList<SealedWalSegment>? CloudBacklog = null,
    WalCommitGroupResult? CommitGroup = null,
    WalCommitResult? Commit = null,
    TimeSpan? FsyncElapsed = null);
