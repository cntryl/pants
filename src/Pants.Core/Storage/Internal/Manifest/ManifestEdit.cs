using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     One manifest edit - a single variant name over its value - held as the objects that describe
///     it and written to JSON only at the journal boundary. Building edits as <see cref="JsonElement" />
///     instead would materialize every byte of a large key bound as its own DOM row, then write it
///     out a second time.
/// </summary>
[JsonConverter(typeof(Converter))]
sealed class ManifestEdit(string variant, object value)
{
    public string Variant { get; } = variant;

    /// <summary>
    ///     A <see cref="FileMeta" /> for <c>AddSst</c>, a list of <see cref="ManifestEdit" /> for
    ///     <c>Batch</c>, otherwise an anonymous record or a <see cref="JsonElement" />.
    /// </summary>
    public object Value { get; } = value;

    /// <summary>Adopts an edit already in JSON form, such as one a cloud DDL operation carries.</summary>
    public static ManifestEdit FromElement(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count() != 1)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest edit is malformed.");
        }

        var property = edit.EnumerateObject().Single();
        return new ManifestEdit(property.Name, property.Value.Clone());
    }

    /// <summary>
    ///     Reads one edit from the reader's current token without building a DOM. When
    ///     <paramref name="allowBareBatch" /> is set, a bare array of edits is accepted as a batch, the
    ///     shape older writers journaled.
    /// </summary>
    public static ManifestEdit Read(ref Utf8JsonReader reader, JsonSerializerOptions options, bool allowBareBatch)
    {
        if (reader.TokenType == JsonTokenType.StartArray && allowBareBatch)
        {
            return new ManifestEdit("Batch", ReadBatch(ref reader, options));
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw MalformedShape();
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            throw MalformedShape();
        }

        var variant = reader.GetString()!;
        if (!reader.Read())
        {
            throw MalformedShape();
        }

        object value;
        switch (variant)
        {
            case "AddSst":
                value = JsonSerializer.Deserialize<FileMeta>(ref reader, options) ??
                        throw PantsException.Create(PantsErrorCode.Corruption, "An AddSst edit is empty.");
                break;
            case "Batch" when reader.TokenType == JsonTokenType.StartArray:
                value = ReadBatch(ref reader, options);
                break;
            default:
                value = JsonElement.ParseValue(ref reader);
                break;
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw MalformedShape();
        }

        return new ManifestEdit(variant, value);
    }

    public void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(Variant);
        JsonSerializer.Serialize(writer, Value, Value.GetType(), options);
        writer.WriteEndObject();
    }

    /// <summary>Materializes the edit as a DOM; only for edits that carry no large key bounds.</summary>
    public JsonElement ToElement(JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer, options);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    static List<ManifestEdit> ReadBatch(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var edits = new List<ManifestEdit>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            edits.Add(Read(ref reader, options, false));
        }

        return edits;
    }

    static PantsException MalformedShape() =>
        PantsException.Create(PantsErrorCode.Corruption, "The manifest journal edit shape is invalid.");

    sealed class Converter : JsonConverter<ManifestEdit>
    {
        public override ManifestEdit Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            ManifestEdit.Read(ref reader, options, false);

        public override void Write(Utf8JsonWriter writer, ManifestEdit value, JsonSerializerOptions options) =>
            value.WriteTo(writer, options);
    }
}
