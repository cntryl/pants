using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     A key bound placed in a loosely typed JSON document (an intent log entry), where a bare
///     <c>byte[]</c> would be written as base64. It persists as the same number array as
///     <see cref="FileMeta.SmallestKey" />.
/// </summary>
[JsonConverter(typeof(Converter))]
readonly record struct KeyBoundValue(byte[] Bytes)
{
    public static object? From(byte[]? bytes) => bytes is null ? null : new KeyBoundValue(bytes);

    sealed class Converter : JsonConverter<KeyBoundValue>
    {
        public override KeyBoundValue Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(KeyBoundJsonConverter.ReadBytes(ref reader));

        public override void Write(Utf8JsonWriter writer, KeyBoundValue value, JsonSerializerOptions options) =>
            KeyBoundJsonConverter.WriteBytes(writer, value.Bytes);
    }
}
