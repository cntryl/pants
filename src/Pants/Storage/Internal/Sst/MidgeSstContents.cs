namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record MidgeSstContents(
    IReadOnlyList<MidgeSstEntry> Entries,
    IReadOnlyList<MidgeRangeTombstone> RangeTombstones,
    int DataBlockCount);
