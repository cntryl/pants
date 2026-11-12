namespace Cntryl.Pants.Transactions;

public sealed record PantsSnapshotPin(
    long SnapshotId,
    ulong Sequence,
    TimeSpan Age,
    int ReferenceCount);
