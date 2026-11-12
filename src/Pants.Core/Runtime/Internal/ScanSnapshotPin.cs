namespace Cntryl.Pants.Runtime.Internal;

sealed record ScanSnapshotPin(
    long SnapshotId,
    ulong BeginSequence,
    DateTimeOffset StartedAtUtc,
    DatabaseVersion StartSnapshot) : ISnapshotPin;
