using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>The shared FORMAT 9 metadata frame and bounded named MessagePack codec.</summary>
static class MetadataFrameCodec
{
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;
    const int HeaderBytes = 13;
    const int FrameOverhead = 17;
    static ReadOnlySpan<byte> Magic => "MDGEMD09"u8;

    public static byte[] Encode(byte kind, ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        return Encode(kind, document.RootElement);
    }

    public static byte[] Encode(byte kind, JsonElement value)
    {
        using var stream = new MemoryStream();
        WriteValue(stream, value, null, kind, 0);
        if (stream.Length > MaximumPayloadBytes)
        {
            throw new PantsInvalidArgumentException("Metadata payload exceeds the FORMAT 9 limit.");
        }

        var frame = new byte[checked((int)stream.Length + FrameOverhead)];
        Magic.CopyTo(frame);
        frame[8] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)stream.Length);
        stream.GetBuffer().AsSpan(0, (int)stream.Length).CopyTo(frame.AsSpan(HeaderBytes));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), DiskFormat.Crc32C(frame.AsSpan(0, frame.Length - 4)));
        return frame;
    }

    public static JsonDocument Decode(ReadOnlySpan<byte> bytes, byte expectedKind)
    {
        if (!TryGetFrameLength(bytes, expectedKind, out var frameLength) || frameLength != bytes.Length)
        {
            throw new PantsCorruptionException("Metadata frame is truncated or has trailing bytes.");
        }

        var expected = BinaryPrimitives.ReadUInt32BigEndian(bytes[^4..]);
        if (DiskFormat.Crc32C(bytes[..^4]) != expected)
        {
            throw new PantsCorruptionException("Metadata frame checksum does not match.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            var payload = bytes[HeaderBytes..^4];
            var cursor = 0;
            ReadValue(payload, ref cursor, writer, 0, expectedKind, null);
            if (cursor != payload.Length)
            {
                throw new PantsCorruptionException("Metadata payload has trailing MessagePack values.");
            }
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    /// <summary>Returns false only for a genuine incomplete final frame with a valid magic prefix.</summary>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> bytes, byte expectedKind, out int length)
    {
        length = 0;
        if (!bytes[..Math.Min(bytes.Length, Magic.Length)].SequenceEqual(Magic[..Math.Min(bytes.Length, Magic.Length)]))
        {
            throw new PantsCompatibilityException("Metadata magic is incompatible with FORMAT 9.");
        }

        if (bytes.Length < 9)
        {
            return false;
        }

        if (bytes[8] != expectedKind)
        {
            throw new PantsCorruptionException("Metadata frame kind does not match its authority.");
        }

        if (bytes.Length < HeaderBytes)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
        if (payloadLength > MaximumPayloadBytes)
        {
            throw new PantsCorruptionException("Metadata frame payload exceeds the FORMAT 9 limit.");
        }

        length = checked((int)payloadLength + FrameOverhead);
        return bytes.Length >= length;
    }

    static void WriteValue(Stream stream, JsonElement value, string? property, byte kind, int depth)
    {
        if (depth >= 64)
        {
            throw new PantsCorruptionException("Metadata nesting exceeds the FORMAT 9 limit.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                WriteContainer(stream, properties.Length, 0x80, 15, 0xde, 0xdf);
                foreach (var item in properties)
                {
                    if (property == "next_sst_seqs")
                    {
                        WriteUnsigned(stream, uint.Parse(item.Name, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        WriteString(stream, item.Name);
                    }

                    WriteValue(stream, item.Value, item.Name, kind == 3 && item.Name == "captured" ? (byte)1 : kind, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                if ((kind != 3 && property is "smallest_key" or "largest_key") || property is "bytes" or "Replace")
                {
                    var binary = value.EnumerateArray().Select(static item => item.GetByte()).ToArray();
                    WriteLength(stream, binary.Length, 0xc4, 0xc5, 0xc6);
                    stream.Write(binary);
                    break;
                }

                WriteContainer(stream, value.GetArrayLength(), 0x90, 15, 0xdc, 0xdd);
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(stream, item, null, kind, depth + 1);
                }

                break;
            case JsonValueKind.String:
                WriteString(stream, value.GetString()!);
                break;
            case JsonValueKind.Number:
                if (value.TryGetUInt64(out var number))
                {
                    WriteUnsigned(stream, number);
                }
                else if (value.TryGetInt64(out var signed))
                {
                    stream.WriteByte(0xd3);
                    Span<byte> integerBytes = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(integerBytes, signed);
                    stream.Write(integerBytes);
                }
                else
                {
                    stream.WriteByte(0xcb);
                    Span<byte> numberBytes = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(numberBytes, BitConverter.DoubleToInt64Bits(value.GetDouble()));
                    stream.Write(numberBytes);
                }

                break;
            case JsonValueKind.True:
                stream.WriteByte(0xc3);
                break;
            case JsonValueKind.False:
                stream.WriteByte(0xc2);
                break;
            case JsonValueKind.Null:
                stream.WriteByte(0xc0);
                break;
            default:
                throw new PantsCorruptionException("Metadata value cannot be encoded.");
        }
    }

    static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 32)
        {
            stream.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else
        {
            WriteLength(stream, bytes.Length, 0xd9, 0xda, 0xdb);
        }

        stream.Write(bytes);
    }

    static void WriteContainer(Stream stream, int count, byte compact, int maximumCompact, byte medium, byte large)
    {
        if (count <= maximumCompact)
        {
            stream.WriteByte((byte)(compact | count));
        }
        else
        {
            WriteLength(stream, count, 0, medium, large);
        }
    }

    static void WriteLength(Stream stream, int length, byte small, byte medium, byte large)
    {
        if (small != 0 && length <= byte.MaxValue)
        {
            stream.WriteByte(small);
            stream.WriteByte((byte)length);
        }
        else if (length <= ushort.MaxValue)
        {
            stream.WriteByte(medium);
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)length);
            stream.Write(bytes);
        }
        else
        {
            stream.WriteByte(large);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
            stream.Write(bytes);
        }
    }

    static void WriteUnsigned(Stream stream, ulong value)
    {
        if (value < 128)
        {
            stream.WriteByte((byte)value);
            return;
        }

        var size = value <= byte.MaxValue ? 1 : value <= ushort.MaxValue ? 2 : value <= uint.MaxValue ? 4 : 8;
        stream.WriteByte(size switch { 1 => 0xcc, 2 => 0xcd, 4 => 0xce, _ => 0xcf });
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - size)..]);
    }

    static void ReadValue(ReadOnlySpan<byte> bytes, ref int cursor, Utf8JsonWriter writer, int depth, byte kind, string? property)
    {
        if (depth >= 64 || cursor >= bytes.Length)
        {
            throw new PantsCorruptionException("Metadata MessagePack value is truncated or too deeply nested.");
        }

        var tag = bytes[cursor++];
        if (((kind is 1 or 2 && property is "smallest_key" or "largest_key") || (kind == 5 && property is "bytes" or "Replace")) &&
            tag is not (0xc0 or 0xc4 or 0xc5 or 0xc6))
        {
            throw new PantsCorruptionException("Metadata binary field does not use MessagePack binary encoding.");
        }

        if (kind == 3 && property is "smallest_key" or "largest_key" && tag is 0xc4 or 0xc5 or 0xc6)
        {
            throw new PantsCorruptionException("Runtime SST bounds require arrays rather than binary values.");
        }

        if (tag <= 0x7f)
        {
            writer.WriteNumberValue(tag);
            return;
        }

        if ((tag & 0xe0) == 0xa0 || tag is 0xd9 or 0xda or 0xdb)
        {
            var count = (tag & 0xe0) == 0xa0 ? tag & 31 : ReadLength(bytes, ref cursor, tag == 0xd9 ? 1 : tag == 0xda ? 2 : 4);
            writer.WriteStringValue(ReadString(bytes, ref cursor, count));
            return;
        }

        if ((tag & 0xf0) == 0x80 || tag is 0xde or 0xdf)
        {
            var count = (tag & 0xf0) == 0x80 ? tag & 15 : ReadLength(bytes, ref cursor, tag == 0xde ? 2 : 4);
            if (count > (bytes.Length - cursor) / 2)
            {
                throw new PantsCorruptionException("Metadata map count exceeds the remaining payload.");
            }

            writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                RequireExtent(bytes, cursor, 1);
                if (property == "next_sst_seqs" && bytes[cursor] > 0x7f && bytes[cursor] is not (0xcc or 0xcd or 0xce or 0xcf))
                {
                    throw new PantsCorruptionException("SST allocator maps require unsigned integer keys on wire.");
                }

                var name = ReadMapKey(bytes, ref cursor);
                if (property == "next_sst_seqs" && !uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    throw new PantsCorruptionException("SST allocator family key exceeds the unsigned 32-bit range.");
                }

                if (!names.Add(name))
                {
                    throw new PantsCorruptionException("Metadata map has duplicate fields.");
                }

                writer.WritePropertyName(name);
                ReadValue(bytes, ref cursor, writer, depth + 1, kind == 3 && name == "captured" ? (byte)1 : kind, name);
            }

            writer.WriteEndObject();
            return;
        }

        if ((tag & 0xf0) == 0x90 || tag is 0xdc or 0xdd)
        {
            var count = (tag & 0xf0) == 0x90 ? tag & 15 : ReadLength(bytes, ref cursor, tag == 0xdc ? 2 : 4);
            if (count > bytes.Length - cursor)
            {
                throw new PantsCorruptionException("Metadata array count exceeds the remaining payload.");
            }

            writer.WriteStartArray();
            for (var index = 0; index < count; index++)
            {
                ReadValue(bytes, ref cursor, writer, depth + 1, kind, null);
            }

            writer.WriteEndArray();
            return;
        }

        switch (tag)
        {
            case 0xc0:
                writer.WriteNullValue();
                return;
            case 0xc2:
            case 0xc3:
                writer.WriteBooleanValue(tag == 0xc3);
                return;
            case 0xc4:
            case 0xc5:
            case 0xc6:
                var count = ReadLength(bytes, ref cursor, tag == 0xc4 ? 1 : tag == 0xc5 ? 2 : 4);
                RequireExtent(bytes, cursor, count);
                writer.WriteStartArray();
                foreach (var item in bytes.Slice(cursor, count))
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
                cursor += count;
                return;
            case 0xcc:
            case 0xcd:
            case 0xce:
            case 0xcf:
                writer.WriteNumberValue(ReadUnsigned(bytes, ref cursor, 1 << (tag - 0xcc)));
                return;
            case 0xd0:
            case 0xd1:
            case 0xd2:
            case 0xd3:
                var signedSize = 1 << (tag - 0xd0);
                var raw = ReadUnsigned(bytes, ref cursor, signedSize);
                var signed = signedSize == 8 ? unchecked((long)raw) : (long)(raw << (64 - signedSize * 8)) >> (64 - signedSize * 8);
                writer.WriteNumberValue(signed);
                return;
            case 0xcb:
                writer.WriteNumberValue(BitConverter.Int64BitsToDouble(unchecked((long)ReadUnsigned(bytes, ref cursor, 8))));
                return;
            default:
                if (tag >= 0xe0)
                {
                    writer.WriteNumberValue(unchecked((sbyte)tag));
                    return;
                }

                throw new PantsCorruptionException("Metadata contains an unsupported MessagePack marker.");
        }
    }

    static string ReadMapKey(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        RequireExtent(bytes, cursor, 1);
        var tag = bytes[cursor++];
        if (tag <= 0x7f)
        {
            return tag.ToString(CultureInfo.InvariantCulture);
        }

        if (tag is >= 0xcc and <= 0xcf)
        {
            return ReadUnsigned(bytes, ref cursor, 1 << (tag - 0xcc)).ToString(CultureInfo.InvariantCulture);
        }

        var length = (tag & 0xe0) == 0xa0 ? tag & 31 : tag switch
        {
            0xd9 => ReadLength(bytes, ref cursor, 1),
            0xda => ReadLength(bytes, ref cursor, 2),
            0xdb => ReadLength(bytes, ref cursor, 4),
            _ => throw new PantsCorruptionException("Metadata map key must be a string or unsigned integer.")
        };
        return ReadString(bytes, ref cursor, length);
    }

    static string ReadString(ReadOnlySpan<byte> bytes, ref int cursor, int count)
    {
        RequireExtent(bytes, cursor, count);
        try
        {
            var result = new UTF8Encoding(false, true).GetString(bytes.Slice(cursor, count));
            cursor += count;
            return result;
        }
        catch (DecoderFallbackException exception)
        {
            throw new PantsCorruptionException("Metadata string is not valid UTF-8.", exception);
        }
    }

    static int ReadLength(ReadOnlySpan<byte> bytes, ref int cursor, int size)
    {
        var value = ReadUnsigned(bytes, ref cursor, size);
        return value <= int.MaxValue ? (int)value : throw new PantsCorruptionException("Metadata length overflows.");
    }

    static ulong ReadUnsigned(ReadOnlySpan<byte> bytes, ref int cursor, int size)
    {
        RequireExtent(bytes, cursor, size);
        ulong result = 0;
        for (var index = 0; index < size; index++)
        {
            result = (result << 8) | bytes[cursor++];
        }

        return result;
    }

    static void RequireExtent(ReadOnlySpan<byte> bytes, int cursor, int count)
    {
        if (count < 0 || count > bytes.Length - cursor)
        {
            throw new PantsCorruptionException("Metadata MessagePack extent is truncated.");
        }
    }
}
