namespace Cntryl.Pants.Transactions;

public sealed record PantsSnapshotPin(
    long SnapshotId,
    long Sequence,
    TimeSpan Age,
    int ReferenceCount);
