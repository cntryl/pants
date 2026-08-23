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
        _lz4 = SstBlockCodec.CompressWithTrailer(_block, CompressionAlgorithm.Lz4);
        _zstd3 = SstBlockCodec.CompressWithTrailer(_block, CompressionAlgorithm.Zstd3);
    }

    [Benchmark]
    public byte[] CompressLz4() =>
        SstBlockCodec.CompressWithTrailer(_block, CompressionAlgorithm.Lz4);

    [Benchmark]
    public byte[] CompressZstd3() =>
        SstBlockCodec.CompressWithTrailer(_block, CompressionAlgorithm.Zstd3);

    [Benchmark]
    public byte[] DecompressLz4() => SstBlockCodec.DecompressWithTrailer(_lz4);

    [Benchmark]
    public byte[] DecompressZstd3() => SstBlockCodec.DecompressWithTrailer(_zstd3);
}
