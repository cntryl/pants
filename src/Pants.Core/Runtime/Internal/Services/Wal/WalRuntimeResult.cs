namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

readonly record struct WalRuntimeResult(
    SealedWalSegment? SealedSegment = null,
    ulong? LocalMaximumSequence = null,
    SealedWalBacklogBatch? CloudBacklog = null,
    WalCommitGroupResult? CommitGroup = null,
    WalCommitResult? Commit = null,
    TimeSpan? FsyncElapsed = null);
