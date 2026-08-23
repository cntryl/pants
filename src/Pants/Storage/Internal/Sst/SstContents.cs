namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record SstContents(
    IReadOnlyList<SstEntry> Entries,
    IReadOnlyList<RangeTombstone> RangeTombstones,
    int DataBlockCount);
