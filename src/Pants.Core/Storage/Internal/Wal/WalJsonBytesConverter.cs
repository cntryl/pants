using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalJsonBytesConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A WAL digest must be an array.");
        }

        var bytes = new List<byte>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            bytes.Add(reader.GetByte());
            if (bytes.Count > 16)
            {
                throw new JsonException("A WAL digest exceeds 16 bytes.");
            }
        }

        if (bytes.Count != 16)
        {
            throw new JsonException("A WAL digest must contain 16 bytes.");
        }

        return bytes.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteNumberValue(item);
        }

        writer.WriteEndArray();
    }
}
