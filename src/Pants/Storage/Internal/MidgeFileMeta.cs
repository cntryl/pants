using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

sealed class MidgeFileMeta
{
    public string Name { get; set; } = string.Empty;

    public uint Level { get; set; }

    public ulong SizeBytes { get; set; }

    [JsonPropertyName("content_crc32c")] public uint? ContentCrc32C { get; set; }

    [JsonPropertyName("cf_id")] public uint ColumnFamilyId { get; set; }

    [JsonPropertyName("sst_seq")] public ulong SstSequence { get; set; }

    public int[]? SmallestKey { get; set; }

    public int[]? LargestKey { get; set; }

    [JsonPropertyName("smallest_seq")] public ulong? SmallestSequence { get; set; }

    [JsonPropertyName("largest_seq")] public ulong? LargestSequence { get; set; }

    public uint Sublevel { get; set; }

    public MidgeFileMeta Clone() => new()
    {
        Name = Name,
        Level = Level,
        SizeBytes = SizeBytes,
        ContentCrc32C = ContentCrc32C,
        ColumnFamilyId = ColumnFamilyId,
        SstSequence = SstSequence,
        SmallestKey = SmallestKey?.ToArray(),
        LargestKey = LargestKey?.ToArray(),
        SmallestSequence = SmallestSequence,
        LargestSequence = LargestSequence,
        Sublevel = Sublevel
    };
}
