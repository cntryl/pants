namespace Pants;

sealed record ProviderCloudHydrationResult(
    IReadOnlyDictionary<ulong, ProviderPublishedWalSegment> PublishedWalSegments,
    ulong CloudDurableSequence)
{
    public static ProviderCloudHydrationResult Empty { get; } =
        new(new Dictionary<ulong, ProviderPublishedWalSegment>(), 0);
}
