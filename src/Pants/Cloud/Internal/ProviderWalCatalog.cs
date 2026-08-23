namespace Cntryl.Pants.Cloud.Internal;

sealed record ProviderWalCatalog
{
    public uint FormatVersion { get; init; } = 1;

    public ulong FencingEpoch { get; init; }

    public SortedDictionary<ulong, ProviderPublishedWalSegment> Segments { get; init; } = [];
}
