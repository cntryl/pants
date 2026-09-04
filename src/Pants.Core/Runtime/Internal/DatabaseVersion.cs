using System.Collections.Immutable;

namespace Cntryl.Pants.Runtime.Internal;

sealed record DatabaseVersion(
    long Sequence,
    ImmutableDictionary<ColumnFamilyIdentity, ImmutableSortedDictionary<byte[], CellState>> Families,
    ImmutableDictionary<ColumnFamilyIdentity, ImmutableArray<CommittedRangeTombstone>> RangeTombstones,
    ImmutableDictionary<string, int> ActiveColumnFamilyVersions,
    ImmutableDictionary<uint, ImmutableArray<FileMeta>> VisibleFiles)
{
    /// <summary>
    ///     Manifest-visible SST files for <paramref name="columnFamilyId" /> at the moment this
    ///     snapshot was taken (independent of later flush/compaction publications), or an empty
    ///     array if the family has no published files.
    /// </summary>
    public ImmutableArray<FileMeta> GetVisibleFiles(uint columnFamilyId) =>
        VisibleFiles.TryGetValue(columnFamilyId, out var files) ? files : [];
}
