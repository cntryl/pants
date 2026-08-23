using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;
using Cntryl.Pants.Storage.Internal.Sst.Compression;

namespace Cntryl.Pants.Benches.Tier1;

public class CompressionBenchmarks : Tier1Benchmark
{
    byte[] _block = null!;
    byte[] _lz4 = null!;
    byte[] _zstd3 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _block = BenchmarkData.Value(64 * 1024);
        _lz4 = MidgeSstBlockCodec.CompressWithTrailer(_block, MidgeCompressionAlgorithm.Lz4);
        _zstd3 = MidgeSstBlockCodec.CompressWithTrailer(_block, MidgeCompressionAlgorithm.Zstd3);
    }

    [Benchmark]
    public byte[] CompressLz4() =>
        MidgeSstBlockCodec.CompressWithTrailer(_block, MidgeCompressionAlgorithm.Lz4);

    [Benchmark]
    public byte[] CompressZstd3() =>
        MidgeSstBlockCodec.CompressWithTrailer(_block, MidgeCompressionAlgorithm.Zstd3);

    [Benchmark]
    public byte[] DecompressLz4() => MidgeSstBlockCodec.DecompressWithTrailer(_lz4);

    [Benchmark]
    public byte[] DecompressZstd3() => MidgeSstBlockCodec.DecompressWithTrailer(_zstd3);
}
