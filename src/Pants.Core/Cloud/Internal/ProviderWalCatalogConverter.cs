using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderWalCatalogConverter : JsonConverter<ProviderWalCatalog>
{
    public override ProviderWalCatalog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var version = root.GetProperty("format_version").GetUInt32();
        if (version != 3)
        {
            throw new PantsCompatibilityException($"Cloud WAL catalog version {version} is unsupported.");
        }

        RequireFields(root, "format_version", "fencing_epoch", "anchor", "next_segment_id", "seq_floor", "retired_by", "segments");
        var epoch = root.GetProperty("fencing_epoch").GetUInt64();
        var anchor = ReadLink(root.GetProperty("anchor"));
        var next = root.GetProperty("next_segment_id").GetUInt64();
        var retiredBy = root.GetProperty("retired_by").GetUInt64();
        var segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>();
        var previous = anchor;
        var previousEpoch = 0UL;
        foreach (var entry in root.GetProperty("segments").EnumerateArray())
        {
            RequireFields(entry, "epoch", "prev", "facts", "size_bytes", "crc32c");
            var entryEpoch = entry.GetProperty("epoch").GetUInt64();
            var prev = ReadLink(entry.GetProperty("prev"));
            var factsElement = entry.GetProperty("facts");
            RequireFields(factsElement, "link", "record_count", "data_len", "max_seq_per_cf");
            _ = ReadLink(factsElement.GetProperty("link"));
            var facts = factsElement.Deserialize<WalSealedFacts>(WalLedgerStore.JsonOptions)!;
            var size = entry.GetProperty("size_bytes").GetUInt64();
            if (!prev.Matches(previous) || facts.Link.Id <= previous.Id || facts.Link.LastSeq <= previous.LastSeq ||
                entryEpoch < previousEpoch || entryEpoch > epoch || facts.RecordCount == 0 ||
                facts.DataLen < 128 || facts.DataLen >= size || facts.MaxSeqPerCf is null || facts.MaxSeqPerCf.Length == 0 ||
                facts.RecordCount < (ulong)facts.MaxSeqPerCf.Length)
            {
                throw new PantsCorruptionException("Cloud WAL catalog chain is invalid.");
            }

            ulong? lastFamily = null;
            ulong max = 0;
            foreach (var row in facts.MaxSeqPerCf)
            {
                if (row is null || row.Length != 2 || row[0] > uint.MaxValue || (lastFamily.HasValue && row[0] <= lastFamily.Value) ||
                    row[1] == 0 || row[1] > facts.Link.LastSeq)
                {
                    throw new PantsCorruptionException("Cloud WAL family coverage is invalid.");
                }

                lastFamily = row[0];
                max = Math.Max(max, row[1]);
            }

            if (max != facts.Link.LastSeq)
            {
                throw new PantsCorruptionException("Cloud WAL coverage does not reach its last sequence.");
            }

            segments.Add(facts.Link.Id, new ProviderPublishedWalSegment
            {
                SegmentId = facts.Link.Id,
                WriterEpoch = entryEpoch,
                MaximumSequence = facts.Link.LastSeq,
                SizeBytes = size,
                ContentCrc32C = entry.GetProperty("crc32c").GetUInt32(),
                ObjectKey = PantsCloudObjectLayout.WalSegmentObjectKey(entryEpoch, facts.Link.Id),
                Previous = prev,
                Facts = facts
            });
            previous = facts.Link;
            previousEpoch = entryEpoch;
        }

        if (epoch == 0 || next <= previous.Id || anchor.Id == 0 && !anchor.Matches(WalChainLink.Genesis) ||
            (anchor.Matches(WalChainLink.Genesis) != (retiredBy == 0)))
        {
            throw new PantsCorruptionException("Cloud WAL catalog authority is invalid.");
        }

        return new ProviderWalCatalog
        {
            FencingEpoch = epoch,
            Anchor = anchor,
            NextSegmentId = next,
            SequenceFloor = root.GetProperty("seq_floor").GetUInt64(),
            RetiredBy = retiredBy,
            WalHistoryStartSegmentId = segments.Count == 0 ? next : segments.Keys.Min(),
            Segments = segments
        };
    }

    static WalChainLink ReadLink(JsonElement value)
    {
        RequireFields(value, "id", "last_seq", "digest");
        var link = value.Deserialize<WalChainLink>(WalLedgerStore.JsonOptions)!;
        if (link.Digest is not { Length: 16 })
        {
            throw new JsonException("A WAL catalog link must contain a 16-byte digest.");
        }

        return link;
    }

    static void RequireFields(JsonElement value, params ReadOnlySpan<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A WAL catalog structure must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new JsonException("A WAL catalog structure contains an unknown or duplicate field.");
            }
        }

        if (seen.Count != fields.Length)
        {
            throw new JsonException("A WAL catalog structure is missing a required field.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ProviderWalCatalog value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("format_version", 3);
        writer.WriteNumber("fencing_epoch", value.FencingEpoch);
        writer.WritePropertyName("anchor");
        JsonSerializer.Serialize(writer, value.Anchor, WalLedgerStore.JsonOptions);
        writer.WriteNumber("next_segment_id", Math.Max(value.NextSegmentId, value.Segments.Count == 0 ? checked(value.Anchor.Id + 1) : checked(value.Segments.Keys.Max() + 1)));
        writer.WriteNumber("seq_floor", value.SequenceFloor);
        writer.WriteNumber("retired_by", value.RetiredBy);
        writer.WriteStartArray("segments");
        foreach (var segment in value.Segments.Values)
        {
            if (segment.Facts is null || segment.Previous is null)
            {
                throw new PantsCorruptionException("Cloud WAL publication requires linked sealed segment facts.");
            }

            writer.WriteStartObject();
            writer.WriteNumber("epoch", segment.WriterEpoch);
            writer.WritePropertyName("prev");
            JsonSerializer.Serialize(writer, segment.Previous, WalLedgerStore.JsonOptions);
            writer.WritePropertyName("facts");
            JsonSerializer.Serialize(writer, segment.Facts, WalLedgerStore.JsonOptions);
            writer.WriteNumber("size_bytes", segment.SizeBytes);
            writer.WriteNumber("crc32c", segment.ContentCrc32C);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
