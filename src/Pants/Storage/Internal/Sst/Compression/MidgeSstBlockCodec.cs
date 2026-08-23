using System.Buffers.Binary;
using ZstdSharp;

namespace Cntryl.Pants.Storage.Internal.Sst.Compression;

static class MidgeSstBlockCodec
{
    internal const int TrailerSize = 5;

    const int MinimumCompressionInputSize = 256;
    const int AdaptiveMinimumSavings = 256;
    const float AdaptiveMaximumRatio = 0.95F;

    internal static MidgeCompressionAlgorithm ParseAlgorithm(byte code) => code switch
    {
        0 => MidgeCompressionAlgorithm.None,
        1 => MidgeCompressionAlgorithm.Lz4,
        2 => MidgeCompressionAlgorithm.Zstd3,
        3 => MidgeCompressionAlgorithm.Zstd9,
        _ => throw new PantsCorruptionException(
            $"unknown compression algorithm code: {code}")
    };

    internal static byte[] CompressWithTrailer(
        ReadOnlySpan<byte> data,
        MidgeCompressionAlgorithm algorithm)
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
        var computedCrc = MidgeDiskFormat.Crc32C(block[..crcOffset]);
        if (storedCrc != computedCrc)
        {
            throw new PantsCorruptionException(
                $"SST block CRC32C mismatch: stored {storedCrc:#010x}, computed {computedCrc:#010x}.");
        }

        var payloadLength = block.Length - TrailerSize;
        var algorithm = ParseAlgorithm(block[payloadLength]);
        try
        {
            return MidgeDiskFormat.Decompress(block[..payloadLength], (byte)algorithm);
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

    internal static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) Compress(
        ReadOnlySpan<byte> data,
        PantsPerformanceGoal performanceGoal)
    {
        if (data.Length < MinimumCompressionInputSize)
        {
            return (data.ToArray(), MidgeCompressionAlgorithm.None);
        }

        return performanceGoal switch
        {
            PantsPerformanceGoal.Latency => CompressWithoutThreshold(data, MidgeCompressionAlgorithm.Lz4),
            PantsPerformanceGoal.Throughput => CompressAdaptive(data),
            PantsPerformanceGoal.Economy => CompressWithoutThreshold(data, MidgeCompressionAlgorithm.Zstd9),
            _ => throw new PantsInternalException($"Unknown performance goal '{performanceGoal}'.")
        };
    }

    static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) Compress(
        ReadOnlySpan<byte> data,
        MidgeCompressionAlgorithm algorithm) =>
        data.Length < MinimumCompressionInputSize
            ? (data.ToArray(), MidgeCompressionAlgorithm.None)
            : CompressWithoutThreshold(data, algorithm);

    static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressWithoutThreshold(
        ReadOnlySpan<byte> data,
        MidgeCompressionAlgorithm algorithm) => algorithm switch
        {
            MidgeCompressionAlgorithm.None => (data.ToArray(), MidgeCompressionAlgorithm.None),
            MidgeCompressionAlgorithm.Lz4 => CompressLz4(data),
            MidgeCompressionAlgorithm.Zstd3 => CompressZstd(data, 3, MidgeCompressionAlgorithm.Zstd3),
            MidgeCompressionAlgorithm.Zstd9 => CompressZstd(data, 9, MidgeCompressionAlgorithm.Zstd9),
            _ => throw new PantsInternalException($"Unknown compression algorithm '{algorithm}'.")
        };

    static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressAdaptive(
        ReadOnlySpan<byte> data)
    {
        var candidates = new[]
        {
            CompressLz4(data),
            CompressZstd(data, 3, MidgeCompressionAlgorithm.Zstd3)
        };

        (byte[] Payload, MidgeCompressionAlgorithm Algorithm)? best = null;
        foreach (var candidate in candidates)
        {
            if (!CompressionQualifies(data.Length, candidate.Payload.Length) ||
                (best is { } current && candidate.Payload.Length >= current.Payload.Length))
            {
                continue;
            }

            best = candidate;
        }

        return best ?? (data.ToArray(), MidgeCompressionAlgorithm.None);
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

    static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressLz4(
        ReadOnlySpan<byte> data) =>
        (MidgeLz4Encoder.CompressWithSizePrefix(data), MidgeCompressionAlgorithm.Lz4);

    static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressZstd(
        ReadOnlySpan<byte> data,
        int level,
        MidgeCompressionAlgorithm algorithm)
    {
        using var compressor = new Compressor(level);
        return (compressor.Wrap(data.ToArray()).ToArray(), algorithm);
    }

    static byte[] AppendTrailer(byte[] payload, MidgeCompressionAlgorithm algorithm)
    {
        var block = new byte[payload.Length + TrailerSize];
        payload.CopyTo(block, 0);
        block[payload.Length] = (byte)algorithm;
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(payload.Length + 1),
            MidgeDiskFormat.Crc32C(block.AsSpan(0, payload.Length + 1)));
        return block;
    }
}
