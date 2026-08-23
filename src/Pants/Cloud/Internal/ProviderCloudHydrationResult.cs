namespace Cntryl.Pants.Cloud.Internal;

sealed record ProviderCloudHydrationResult(
    IReadOnlyDictionary<ulong, ProviderPublishedWalSegment> PublishedWalSegments,
    ulong CloudDurableSequence,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RecoverySsts,
    bool RequiresSalvage)
{
    public static ProviderCloudHydrationResult Empty { get; } =
        new(
            new Dictionary<ulong, ProviderPublishedWalSegment>(),
            0,
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal),
            false);
}
