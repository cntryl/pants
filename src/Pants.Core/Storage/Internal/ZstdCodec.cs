using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Standard Zstandard frame compression backed by the BCL <c>System.IO.Compression</c> types.
///     The frame format is the interchange contract with Midge, so no library-specific framing is added.
/// </summary>
static class ZstdCodec
{
    const uint FrameMagic = 0xFD2FB528;

    public static byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        var destination = new byte[checked((int)ZstandardEncoder.GetMaxCompressedLength(data.Length))];
        // The one-shot path pledges the source size, so the frame header records its content size.
        if (!ZstandardEncoder.TryCompress(data, destination, out var written, level, 0))
        {
            throw new InvalidOperationException("Zstd compression failed.");
        }

        return destination.AsSpan(0, written).ToArray();
    }

    /// <summary>
    ///     Reads the frame content size from the header without decoding the body. Returns
    ///     <see langword="null" /> when the frame omits it.
    /// </summary>
    public static ulong? GetContentSize(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(frame) != FrameMagic)
        {
            throw new InvalidDataException("Zstd frame header is invalid.");
        }

        var descriptor = frame[4];
        var singleSegment = (descriptor & 0x20) != 0;
        var cursor = 5 + (singleSegment ? 0 : 1) + ((descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 });
        var sizeBytes = (descriptor >> 6) switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        if (sizeBytes == 0)
        {
            return null;
        }

        if (cursor > frame.Length || sizeBytes > frame.Length - cursor)
        {
            throw new InvalidDataException("Zstd frame content size is truncated.");
        }

        return sizeBytes switch
        {
            1 => frame[cursor],
            2 => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(frame[cursor..]) + 256,
            4 => BinaryPrimitives.ReadUInt32LittleEndian(frame[cursor..]),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(frame[cursor..])
        };
    }

    /// <summary>
    ///     Decodes one frame, refusing to produce more than <paramref name="maximumBytes" />.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> frame, int maximumBytes)
    {
        var declared = GetContentSize(frame);
        if (declared is > int.MaxValue || declared > (ulong)maximumBytes)
        {
            throw new InvalidDataException("Zstd payload exceeds the decompression limit.");
        }

        // Unknown-size frames start small and grow, bounded by the limit.
        var buffer = new byte[declared is { } size ? (int)size : Math.Min(maximumBytes, 64 * 1024)];
        var total = 0;
        var consumed = 0;
        using var decoder = new ZstandardDecoder();
        while (true)
        {
            var status = decoder.Decompress(frame[consumed..], buffer.AsSpan(total), out var read, out var written);
            consumed += read;
            total += written;
            switch (status)
            {
                case OperationStatus.Done:
                    if (declared is { } expected && (ulong)total != expected)
                    {
                        throw new InvalidDataException("Zstd payload length does not match its frame header.");
                    }

                    return total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
                case OperationStatus.DestinationTooSmall:
                    if (total >= maximumBytes || declared is not null)
                    {
                        throw new InvalidDataException("Zstd payload exceeds the decompression limit.");
                    }

                    Array.Resize(ref buffer, (int)Math.Min((long)maximumBytes, (long)buffer.Length * 2));
                    break;
                case OperationStatus.NeedMoreData:
                    throw new InvalidDataException("Zstd payload is truncated.");
                default:
                    throw new InvalidDataException("Zstd payload is invalid.");
            }
        }
    }
}
