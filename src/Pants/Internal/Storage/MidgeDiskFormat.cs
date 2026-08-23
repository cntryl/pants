using System.Buffers.Binary;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace Pants;

internal static class MidgeDiskFormat
{
    public const int FormatVersion = 3;
    public const int WalMaximumRecordBytes = 64 * 1024 * 1024;
    public const uint SstFormatVersion = 4;
    public const int SstFooterSize = 84;
    public const ulong SstFooterMagic = 0xdb47_7524_8b80_fb57;

    private static readonly uint[] CrcTable = CreateCrcTable();

    public static uint Crc32C(ReadOnlySpan<byte> bytes) => Crc32CAppend(0, bytes);

    public static uint Crc32CAppend(uint checksum, ReadOnlySpan<byte> bytes)
    {
        var crc = ~checksum;
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> bytes, byte algorithm)
    {
        switch (algorithm)
        {
            case 0:
                return bytes.ToArray();
            case 1:
                if (bytes.Length < 4)
                {
                    throw new PantsStorageException("Midge LZ4 payload has no size prefix.");
                }

                int decodedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
                if (decodedLength > 64 * 1024 * 1024)
                {
                    throw new PantsStorageException("Midge LZ4 payload exceeds the decompression limit.");
                }

                var decoded = new byte[decodedLength];
                int actualLength = LZ4Codec.Decode(bytes[4..], decoded);
                if (actualLength != decodedLength)
                {
                    throw new PantsStorageException("Midge LZ4 payload could not be decompressed.");
                }

                return decoded;
            case 2:
            case 3:
                using (var decompressor = new Decompressor())
                {
                    Span<byte> output = decompressor.Unwrap(bytes.ToArray());
                    if (output.Length > 64 * 1024 * 1024)
                    {
                        throw new PantsStorageException("Midge Zstd payload exceeds the decompression limit.");
                    }

                    return output.ToArray();
                }
            default:
                throw new PantsStorageException(
                    $"Midge compression algorithm '{algorithm}' is unsupported.");
        }
    }

    private static uint[] CreateCrcTable()
    {
        const uint polynomial = 0x82f63b78;
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint current = index;
            for (int bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? (current >> 1) ^ polynomial
                    : current >> 1;
            }

            table[index] = current;
        }

        return table;
    }
}
