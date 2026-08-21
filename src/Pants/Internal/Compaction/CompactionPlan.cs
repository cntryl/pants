namespace Pants;

internal sealed record CompactionPlan(
    uint SourceLevel,
    uint TargetLevel,
    IReadOnlyList<MidgeFileMeta> Inputs);
