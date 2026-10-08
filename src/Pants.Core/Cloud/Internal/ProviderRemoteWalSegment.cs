namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderRemoteWalSegment(
    ICloudObjectStore walStore,
    ProviderPublishedWalSegment segment,
    int pageBytes) : IRemoteWalSegment
{
    public ulong SegmentId => segment.SegmentId;

    public string Name => segment.ObjectKey;

    public ulong SizeBytes => segment.SizeBytes;

    public Stream OpenRead() =>
        new RemoteRangeStream(walStore, segment.ObjectKey, checked((long)segment.SizeBytes), pageBytes);
}
