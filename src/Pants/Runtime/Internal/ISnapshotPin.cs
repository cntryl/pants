namespace Cntryl.Pants;

internal interface ISnapshotPin
{
    long SnapshotId { get; }

    long BeginSequence { get; }

    DateTimeOffset StartedAtUtc { get; }

    DatabaseSnapshot StartSnapshot { get; }
}
