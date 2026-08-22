using System.Text.Json.Serialization;

namespace Pants;

sealed record ProviderPublishedWalSegment
{
    public ulong SegmentId { get; init; }

    public ulong WriterEpoch { get; init; }

    [JsonPropertyName("max_sequence")]
    public ulong MaximumSequence { get; init; }

    public ulong SizeBytes { get; init; }

    [JsonPropertyName("content_crc32c")]
    public uint ContentCrc32C { get; init; }

    public string ObjectKey { get; init; } = string.Empty;
}
