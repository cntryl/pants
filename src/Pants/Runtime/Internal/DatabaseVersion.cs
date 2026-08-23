using System.Collections.Immutable;

namespace Cntryl.Pants.Runtime.Internal;

sealed record DatabaseVersion(
    long Sequence,
    ImmutableDictionary<ColumnFamilyIdentity, ImmutableSortedDictionary<byte[], CellState>> Families,
    ImmutableDictionary<ColumnFamilyIdentity, ImmutableArray<CommittedRangeTombstone>> RangeTombstones,
    ImmutableDictionary<string, int> ActiveColumnFamilyVersions);
