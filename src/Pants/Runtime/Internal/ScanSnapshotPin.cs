namespace Cntryl.Pants.Runtime.Internal;

sealed record ScanSnapshotPin(
    long SnapshotId,
    long BeginSequence,
    DateTimeOffset StartedAtUtc,
    DatabaseVersion StartSnapshot) : ISnapshotPin;
