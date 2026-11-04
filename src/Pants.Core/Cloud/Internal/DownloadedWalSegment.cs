namespace Cntryl.Pants.Cloud.Internal;

/// <summary>A catalog-authorized segment staged for providers without ranged reads.</summary>
sealed class DownloadedWalSegment(ulong segmentId, string path) : IRemoteWalSegment
{
    public ulong SegmentId { get; } = segmentId;

    public string Name { get; } = path;

    public ulong SizeBytes => checked((ulong)new FileInfo(Name).Length);

    public Stream OpenRead(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoragePathGuard.EnsureNotLink(Name);
        return new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
