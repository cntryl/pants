namespace Cntryl.Pants.Runtime.Internal;

interface ISnapshotPin
{
    long SnapshotId { get; }

    long BeginSequence { get; }

    DateTimeOffset StartedAtUtc { get; }

    DatabaseVersion StartSnapshot { get; }
}
