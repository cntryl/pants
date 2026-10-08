namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     A catalog-authorized WAL segment that recovery replays straight from remote storage, without
///     staging a local copy, so a backlog larger than the local disk can still be recovered.
/// </summary>
interface IRemoteWalSegment
{
    ulong SegmentId { get; }

    string Name { get; }

    ulong SizeBytes { get; }

    /// <summary>Opens a seekable read-only stream that fetches bounded ranges on demand.</summary>
    Stream OpenRead();
}
