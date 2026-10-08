namespace Cntryl.Pants.Cloud.Internal;

sealed record ProviderCloudHydrationResult(
    IReadOnlyDictionary<ulong, ProviderPublishedWalSegment> PublishedWalSegments,
    ulong CloudDurableSequence,
    bool RequiresSalvage,
    IReadOnlyList<IRemoteWalSegment>? RemoteWalSegments = null)
{
    public static ProviderCloudHydrationResult Empty { get; } =
        new(
            new Dictionary<ulong, ProviderPublishedWalSegment>(),
            0,
            false);
}
