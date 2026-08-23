namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

sealed record CompactionMergeResult(
    IReadOnlyList<SstEntry> Entries,
    IReadOnlyList<RangeTombstone> RangeTombstones);
