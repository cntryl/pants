using System.Buffers;
using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Recovery;

/// <summary>
///     One entry of the intent log, held as its JSON bytes. The entries the engine inspects only
///     need their names, phases and file lists, so <see cref="Summary" /> is a DOM with the key
///     bound arrays left empty: a bound of tens of megabytes would otherwise cost a DOM row per
///     byte every time the log is loaded or filtered. The bytes themselves are what is persisted.
/// </summary>
sealed class IntentEntry
{
    readonly byte[] _json;
    JsonElement? _summary;

    IntentEntry(byte[] json) => _json = json;

    /// <summary>Everything except the key bounds, which read as empty arrays.</summary>
    public JsonElement Summary => _summary ??= BuildSummary(_json);

    public static IntentEntry Create(string variant, object value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(variant);
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
            writer.WriteEndObject();
        }

        return new IntentEntry(buffer.WrittenSpan.ToArray());
    }

    public static IntentEntry FromJson(ReadOnlySpan<byte> json) => new(json.ToArray());

    public void WriteTo(Utf8JsonWriter writer)
    {
        var reader = new Utf8JsonReader(_json);
        _ = reader.Read();
        CopyValue(ref reader, writer, false);
    }

    /// <summary>The entry with the phase inside its variant object replaced, or appended if absent.</summary>
    public IntentEntry WithPhase(string phase)
    {
        var reader = new Utf8JsonReader(_json);
        if (!reader.Read() ||
            reader.TokenType != JsonTokenType.StartObject ||
            !reader.Read() ||
            reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("An intent log entry must be an object with one variant.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(reader.GetString()!);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("An intent log entry variant must be an object.");
            }

            writer.WriteStartObject();
            var phaseWritten = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                _ = reader.Read();
                if (name == "phase")
                {
                    reader.Skip();
                    writer.WriteString(name, phase);
                    phaseWritten = true;
                    continue;
                }

                writer.WritePropertyName(name);
                CopyValue(ref reader, writer, false);
            }

            if (!phaseWritten)
            {
                writer.WriteString("phase", phase);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new IntentEntry(buffer.WrittenSpan.ToArray());
    }

    static JsonElement BuildSummary(byte[] json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var reader = new Utf8JsonReader(json);
            _ = reader.Read();
            CopyValue(ref reader, writer, true);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    ///     Copies the value at the reader's current token to the writer, leaving the reader on its
    ///     last token. Key bound arrays are skipped and written empty when
    ///     <paramref name="elideKeyBounds" /> is set.
    /// </summary>
    static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer, bool elideKeyBounds)
    {
        var depth = 0;
        do
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    depth--;
                    break;
                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    depth++;
                    break;
                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    depth--;
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    writer.WritePropertyName(name);
                    if (elideKeyBounds &&
                        (name is "smallest_key" or "largest_key") &&
                        reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            reader.Skip();
                            writer.WriteStartArray();
                            writer.WriteEndArray();
                        }
                        else
                        {
                            CopyValue(ref reader, writer, false);
                        }
                    }

                    break;
                case JsonTokenType.String:
                    writer.WriteStringValue(reader.GetString());
                    break;
                case JsonTokenType.Number:
                    // Integers go through the number writer so that indentation matches what
                    // serializing the entry directly produces; anything else is kept verbatim.
                    if (reader.TryGetInt64(out var integer))
                    {
                        writer.WriteNumberValue(integer);
                    }
                    else if (reader.TryGetUInt64(out var unsignedInteger))
                    {
                        writer.WriteNumberValue(unsignedInteger);
                    }
                    else
                    {
                        writer.WriteRawValue(reader.ValueSpan, true);
                    }

                    break;
                case JsonTokenType.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonTokenType.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonTokenType.Null:
                    writer.WriteNullValue();
                    break;
            }
        }
        while (depth > 0 && reader.Read());
    }
}
