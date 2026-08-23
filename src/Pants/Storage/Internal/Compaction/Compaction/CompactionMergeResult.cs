namespace Cntryl.Pants;

internal sealed record CompactionMergeResult(
    IReadOnlyList<MidgeSstEntry> Entries,
    IReadOnlyList<MidgeRangeTombstone> RangeTombstones);
