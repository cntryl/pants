namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

enum CompactionPurpose
{
    Merge,
    Repair,
    Fragment
}

sealed record CompactionPlan(
    uint SourceLevel,
    uint TargetLevel,
    uint ColumnFamilyId,
    ulong? SnapshotHorizon,
    bool PointTombstoneGcEligible,
    bool RangeTombstoneGcEligible,
    IReadOnlyList<FileMeta> Inputs,
    CompactionPurpose Purpose = CompactionPurpose.Merge,
    byte[]? FragmentSplitKey = null);
