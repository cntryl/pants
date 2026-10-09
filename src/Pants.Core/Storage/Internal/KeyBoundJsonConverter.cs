using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Reads and writes a key bound as the JSON array of byte values the Midge manifest pins, while
///     the engine holds it as <c>byte[]</c>. Writing goes straight to the output and reading fills a
///     single growing buffer, so neither direction builds an intermediate collection per byte.
/// </summary>
sealed class KeyBoundJsonConverter : JsonConverter<byte[]>
{
    const int InitialCapacity = 64;

    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadBytes(ref reader);

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        WriteBytes(writer, value);

    internal static byte[] ReadBytes(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new ManifestKeyBoundFormatException("A manifest key bound must be an array of byte values.");
        }

        var buffer = new byte[InitialCapacity];
        var length = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
            }

            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out var value))
            {
                throw new ManifestKeyBoundFormatException("A manifest key bound must contain only byte values.");
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, checked(buffer.Length * 2));
            }

            buffer[length++] = value;
        }

        throw new ManifestKeyBoundFormatException("A manifest key bound array is not terminated.");
    }

    internal static void WriteBytes(Utf8JsonWriter writer, byte[] value)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteNumberValue(item);
        }

        writer.WriteEndArray();
    }
}
