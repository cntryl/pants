namespace Pants;

internal sealed record MidgeSstContents(
    IReadOnlyList<MidgeSstEntry> Entries,
    IReadOnlyList<MidgeRangeTombstone> RangeTombstones,
    int DataBlockCount);
