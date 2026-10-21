namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

enum CompactionPurpose
{
    Merge,
    Fragment
}

sealed record CompactionPlan(
    uint SourceLevel,
    uint TargetLevel,
    uint ColumnFamilyId,
    long? SnapshotHorizon,
    bool PointTombstoneGcEligible,
    bool RangeTombstoneGcEligible,
    IReadOnlyList<FileMeta> Inputs,
    CompactionPurpose Purpose = CompactionPurpose.Merge,
    byte[]? FragmentSplitKey = null);
