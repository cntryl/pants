namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

sealed record CompactionMergeResult(
    IReadOnlyList<MidgeSstEntry> Entries,
    IReadOnlyList<MidgeRangeTombstone> RangeTombstones);
