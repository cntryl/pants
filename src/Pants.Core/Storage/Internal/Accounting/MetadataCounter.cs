namespace Cntryl.Pants.Storage.Internal.Accounting;

enum MetadataCounter
{
    OperationAttempts,
    OperationFailures,
    AbandonedOperations,
    SnapshotIssuedBytes,
    JournalIssuedBytes,
    OtherIssuedBytes,
    SnapshotReturnedBytes,
    JournalReturnedBytes,
    OtherReturnedBytes,
    SnapshotDurableBytes,
    JournalDurableBytes,
    SnapshotDurableCount,
    CheckpointCompleteCount,
    CheckpointAttempts,
    CheckpointNanoseconds,
    JournalAppendAttempts,
    JournalNanoseconds,
    PublicationAttempts,
    PublicationFailures,
    PublicationAttemptNanoseconds,
    FlushCommittedCount,
    FlushCommittedSstBytes,
    FullPublicationNanoseconds,
    CompactionCommittedCount,
    CompactionCommittedSstBytes
}
