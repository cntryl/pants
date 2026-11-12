using System.Text.Json.Serialization;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Cloud.Internal;

[JsonConverter(typeof(ProviderWalCatalogConverter))]
sealed record ProviderWalCatalog
{
    public uint FormatVersion { get; init; } = 3;

    public WalChainLink Anchor { get; init; } = WalChainLink.Genesis;

    public ulong SequenceFloor { get; init; }

    public ulong RetiredBy { get; init; }

    public ulong FencingEpoch { get; init; }

    /// <summary>
    ///     The next segment identity the catalog can publish. It survives prefix retirement so an
    ///     empty catalog cannot forget identities that already appeared in durable history.
    /// </summary>
    public ulong NextSegmentId { get; init; }

    /// <summary>
    ///     Durable segment identity below which missing catalog entries are covered by the
    ///     manifest history boundary. Older entries may remain until background retirement proves
    ///     and deletes their objects.
    /// </summary>
    public ulong WalHistoryStartSegmentId { get; init; }

    public string? LocalWalCacheId { get; init; }

    public ulong LocalWalCacheEpoch { get; init; }

    public ulong MinimumLocalWalWriterEpoch { get; init; }

    public SortedDictionary<ulong, ProviderPublishedWalSegment> Segments { get; init; } = [];
}
