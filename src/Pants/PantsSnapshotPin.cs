namespace Pants;

public sealed record PantsSnapshotPin(
    long SnapshotId,
    long Sequence,
    TimeSpan Age,
    int ReferenceCount);
