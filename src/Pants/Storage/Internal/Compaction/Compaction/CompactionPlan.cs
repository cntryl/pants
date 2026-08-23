namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

sealed record CompactionPlan(
    uint SourceLevel,
    uint TargetLevel,
    uint ColumnFamilyId,
    long? SnapshotHorizon,
    bool PointTombstoneGcEligible,
    bool RangeTombstoneGcEligible,
    IReadOnlyList<FileMeta> Inputs);
