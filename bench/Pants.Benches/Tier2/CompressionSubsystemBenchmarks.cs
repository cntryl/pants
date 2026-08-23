using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;
using Cntryl.Pants.Storage.Internal.Sst.Compression;

namespace Cntryl.Pants.Benches.Tier2;

public class CompressionSubsystemBenchmarks : Tier2Benchmark
{
    const int BlockCount = 32;
    const int BlockSize = 16 * 1024;
    byte[][] _blocks = null!;
    byte[][] _lz4Blocks = null!;
    byte[][] _zstd3Blocks = null!;

    [GlobalSetup]
    public void Setup()
    {
        _blocks = Enumerable.Range(0, BlockCount).Select(index => Tier2Data.Value(BlockSize, index)).ToArray();
        _lz4Blocks = Compress(_blocks, MidgeCompressionAlgorithm.Lz4);
        _zstd3Blocks = Compress(_blocks, MidgeCompressionAlgorithm.Zstd3);
    }

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public byte[][] CompressLz4Batch() => Compress(_blocks, MidgeCompressionAlgorithm.Lz4);

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public byte[][] CompressZstd3Batch() => Compress(_blocks, MidgeCompressionAlgorithm.Zstd3);

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public byte[][] DecompressLz4Batch() => Decompress(_lz4Blocks);

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public byte[][] DecompressZstd3Batch() => Decompress(_zstd3Blocks);

    static byte[][] Compress(byte[][] blocks, MidgeCompressionAlgorithm algorithm) => blocks
        .Select(block => MidgeSstBlockCodec.CompressWithTrailer(block, algorithm))
        .ToArray();

    static byte[][] Decompress(byte[][] blocks) => blocks
        .Select(block => MidgeSstBlockCodec.DecompressWithTrailer(block))
        .ToArray();
}
