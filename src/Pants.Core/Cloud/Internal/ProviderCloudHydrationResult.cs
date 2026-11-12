using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Cloud.Internal;

sealed record ProviderCloudHydrationResult(
    IReadOnlyDictionary<ulong, ProviderPublishedWalSegment> PublishedWalSegments,
    ulong CloudDurableSequence,
    bool RequiresSalvage,
    IReadOnlyList<IRemoteWalSegment>? RemoteWalSegments = null,
    ulong NextWalSegmentId = 1,
    WalChainLink? WalHead = null,
    ulong SequenceFloor = 0,
    bool RequiresBootstrap = false)
{
    public static ProviderCloudHydrationResult Empty { get; } =
        new(
            new Dictionary<ulong, ProviderPublishedWalSegment>(),
            0,
            false,
            NextWalSegmentId: 1);
}
