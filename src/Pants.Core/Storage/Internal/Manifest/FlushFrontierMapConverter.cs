using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal.Manifest;

sealed class FlushFrontierMapConverter : JsonConverter<Dictionary<uint, ulong>>
{
    public override Dictionary<uint, ulong> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Flush frontiers must be an ordered array.");
        }

        var frontiers = new Dictionary<uint, ulong>();
        uint? previousId = null;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("cf_id", out var idElement) ||
                !idElement.TryGetUInt32(out var id) ||
                !row.TryGetProperty("through_sequence", out var sequenceElement) ||
                !sequenceElement.TryGetUInt64(out var throughSequence) ||
                (previousId.HasValue && id <= previousId.Value))
            {
                throw new JsonException("Flush frontier rows are invalid or out of order.");
            }

            previousId = id;
            frontiers.Add(id, throughSequence);
        }

        if (!frontiers.TryGetValue(0, out var defaultFrontier) || defaultFrontier == ulong.MaxValue)
        {
            throw new JsonException("Flush frontiers require a live default family at sequence zero or later.");
        }

        return frontiers;
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<uint, ulong> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (id, throughSequence) in value.OrderBy(static pair => pair.Key))
        {
            writer.WriteStartObject();
            writer.WriteNumber("cf_id", id);
            writer.WriteNumber("through_sequence", throughSequence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
