using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Benches.Tier1;

public class TlvBenchmarks : Tier1Benchmark
{
    MidgeWalRecord _record8 = null!;
    MidgeWalRecord _record64 = null!;
    MidgeWalRecord _record256 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _record8 = Record(8);
        _record64 = Record(64);
        _record256 = Record(256);
    }

    [Benchmark]
    public byte[] EncodeField8B() => MidgeWalCodec.EncodeRecord(_record8);

    [Benchmark]
    public byte[] EncodeField64B() => MidgeWalCodec.EncodeRecord(_record64);

    [Benchmark]
    public byte[] EncodeField256B() => MidgeWalCodec.EncodeRecord(_record256);

    static MidgeWalRecord Record(int valueSize) => new(
        1,
        MidgeWalOperation.Put,
        BenchmarkData.Key(1),
        BenchmarkData.Value(valueSize),
        1,
        null,
        null,
        null,
        1);
}
