using System.Buffers.Binary;
using ZstdSharp;

namespace Cntryl.Pants.Storage.Internal.Sst.Compression;

static class SstBlockCodec
{
    internal const int TrailerSize = 5;

    const int MinimumCompressionInputSize = 256;
    const int AdaptiveMinimumSavings = 256;
    const float AdaptiveMaximumRatio = 0.95F;

    internal static CompressionAlgorithm ParseAlgorithm(byte code) => code switch
    {
        0 => CompressionAlgorithm.None,
        1 => CompressionAlgorithm.Lz4,
        2 => CompressionAlgorithm.Zstd3,
        3 => CompressionAlgorithm.Zstd9,
        _ => throw new PantsCorruptionException(
            $"unknown compression algorithm code: {code}")
    };

    internal static byte[] CompressWithTrailer(
        ReadOnlySpan<byte> data,
        CompressionAlgorithm algorithm)
    {
        var (payload, emittedAlgorithm) = Compress(data, algorithm);
        return AppendTrailer(payload, emittedAlgorithm);
    }

    internal static byte[] CompressWithTrailer(
        ReadOnlySpan<byte> data,
        PantsPerformanceGoal performanceGoal)
    {
        var (payload, algorithm) = Compress(data, performanceGoal);
        return AppendTrailer(payload, algorithm);
    }

    internal static byte[] DecompressWithTrailer(ReadOnlySpan<byte> block)
    {
        if (block.Length < TrailerSize)
        {
            throw new PantsCorruptionException("SST block is too small for trailer.");
        }

        var crcOffset = block.Length - sizeof(uint);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(block[crcOffset..]);
        var computedCrc = DiskFormat.Crc32C(block[..crcOffset]);
        if (storedCrc != computedCrc)
        {
            throw new PantsCorruptionException(
                $"SST block CRC32C mismatch: stored {storedCrc:#010x}, computed {computedCrc:#010x}.");
        }

        var payloadLength = block.Length - TrailerSize;
        var algorithm = ParseAlgorithm(block[payloadLength]);
        try
        {
            return DiskFormat.Decompress(block[..payloadLength], (byte)algorithm);
        }
        catch (PantsCorruptionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PantsCorruptionException(
                $"SST {algorithm} payload could not be decompressed.",
                exception);
        }
    }

    internal static (byte[] Payload, CompressionAlgorithm Algorithm) Compress(
        ReadOnlySpan<byte> data,
        PantsPerformanceGoal performanceGoal)
    {
        if (data.Length < MinimumCompressionInputSize)
        {
            return (data.ToArray(), CompressionAlgorithm.None);
        }

        return performanceGoal switch
        {
            PantsPerformanceGoal.Latency => CompressWithoutThreshold(data, CompressionAlgorithm.Lz4),
            PantsPerformanceGoal.Throughput => CompressAdaptive(data),
            PantsPerformanceGoal.Economy => CompressWithoutThreshold(data, CompressionAlgorithm.Zstd9),
            _ => throw new PantsInternalException($"Unknown performance goal '{performanceGoal}'.")
        };
    }

    static (byte[] Payload, CompressionAlgorithm Algorithm) Compress(
        ReadOnlySpan<byte> data,
        CompressionAlgorithm algorithm) =>
        data.Length < MinimumCompressionInputSize
            ? (data.ToArray(), CompressionAlgorithm.None)
            : CompressWithoutThreshold(data, algorithm);

    static (byte[] Payload, CompressionAlgorithm Algorithm) CompressWithoutThreshold(
        ReadOnlySpan<byte> data,
        CompressionAlgorithm algorithm) => algorithm switch
        {
            CompressionAlgorithm.None => (data.ToArray(), CompressionAlgorithm.None),
            CompressionAlgorithm.Lz4 => CompressLz4(data),
            CompressionAlgorithm.Zstd3 => CompressZstd(data, 3, CompressionAlgorithm.Zstd3),
            CompressionAlgorithm.Zstd9 => CompressZstd(data, 9, CompressionAlgorithm.Zstd9),
            _ => throw new PantsInternalException($"Unknown compression algorithm '{algorithm}'.")
        };

    static (byte[] Payload, CompressionAlgorithm Algorithm) CompressAdaptive(
        ReadOnlySpan<byte> data)
    {
        var candidates = new[]
        {
            CompressLz4(data),
            CompressZstd(data, 3, CompressionAlgorithm.Zstd3)
        };

        (byte[] Payload, CompressionAlgorithm Algorithm)? best = null;
        foreach (var candidate in candidates)
        {
            if (!CompressionQualifies(data.Length, candidate.Payload.Length) ||
                (best is { } current && candidate.Payload.Length >= current.Payload.Length))
            {
                continue;
            }

            best = candidate;
        }

        return best ?? (data.ToArray(), CompressionAlgorithm.None);
    }

    static bool CompressionQualifies(int originalSize, int compressedSize)
    {
        if (compressedSize >= originalSize || originalSize - compressedSize < AdaptiveMinimumSavings)
        {
            return false;
        }

        var threshold = (double)AdaptiveMaximumRatio;
        var thresholdBits = BitConverter.SingleToInt32Bits(AdaptiveMaximumRatio);
        var next = BitConverter.Int32BitsToSingle(checked(thresholdBits + 1));
        threshold += (next - threshold) / 2D;
        return (double)compressedSize / originalSize <= threshold;
    }

    static (byte[] Payload, CompressionAlgorithm Algorithm) CompressLz4(
        ReadOnlySpan<byte> data) =>
        (Lz4Encoder.CompressWithSizePrefix(data), CompressionAlgorithm.Lz4);

    static (byte[] Payload, CompressionAlgorithm Algorithm) CompressZstd(
        ReadOnlySpan<byte> data,
        int level,
        CompressionAlgorithm algorithm)
    {
        using var compressor = new Compressor(level);
        return (compressor.Wrap(data.ToArray()).ToArray(), algorithm);
    }

    static byte[] AppendTrailer(byte[] payload, CompressionAlgorithm algorithm)
    {
        var block = new byte[payload.Length + TrailerSize];
        payload.CopyTo(block, 0);
        block[payload.Length] = (byte)algorithm;
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(payload.Length + 1),
            DiskFormat.Crc32C(block.AsSpan(0, payload.Length + 1)));
        return block;
    }
}
