namespace Cntryl.Pants.Runtime.Internal;

interface ISnapshotPin
{
    long SnapshotId { get; }

    ulong BeginSequence { get; }

    DateTimeOffset StartedAtUtc { get; }

    DatabaseVersion StartSnapshot { get; }
}
