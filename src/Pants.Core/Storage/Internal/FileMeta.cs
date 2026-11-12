using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

sealed class FileMeta
{
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonRequired]
    public uint Level { get; set; }

    [JsonRequired]
    public ulong SizeBytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("content_crc32c")] public uint? ContentCrc32C { get; set; }

    [JsonPropertyName("cf_id")] public uint ColumnFamilyId { get; set; }

    [JsonPropertyName("sst_seq")] public ulong SstSequence { get; set; }

    /// <summary>
    ///     Held as bytes and persisted as MessagePack binary bounds in manifest frames. Bounds are never mutated in
    ///     place, so copies of this metadata share them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(KeyBoundJsonConverter))]
    public byte[]? SmallestKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(KeyBoundJsonConverter))]
    public byte[]? LargestKey { get; set; }

    [JsonPropertyName("key_bounds_complete")] public bool KeyBoundsComplete { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("smallest_seq")] public ulong? SmallestSequence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("largest_seq")] public ulong? LargestSequence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? EarliestExpirationMillis { get; set; }

    public uint Sublevel { get; set; }

    public bool HasTrustedKeyBounds() =>
        KeyBoundsComplete &&
        SmallestKey is not null &&
        LargestKey is not null &&
        SmallestKey.AsSpan().SequenceCompareTo(LargestKey) <= 0;

    public FileMeta Clone() => new()
    {
        Name = Name,
        Level = Level,
        SizeBytes = SizeBytes,
        ContentCrc32C = ContentCrc32C,
        ColumnFamilyId = ColumnFamilyId,
        SstSequence = SstSequence,
        SmallestKey = SmallestKey,
        LargestKey = LargestKey,
        KeyBoundsComplete = KeyBoundsComplete,
        SmallestSequence = SmallestSequence,
        LargestSequence = LargestSequence,
        EarliestExpirationMillis = EarliestExpirationMillis,
        Sublevel = Sublevel
    };
}
