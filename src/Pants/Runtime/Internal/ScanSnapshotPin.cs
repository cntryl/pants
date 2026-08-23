namespace Cntryl.Pants;

internal sealed record ScanSnapshotPin(
    long SnapshotId,
    long BeginSequence,
    DateTimeOffset StartedAtUtc,
    DatabaseSnapshot StartSnapshot) : ISnapshotPin;
