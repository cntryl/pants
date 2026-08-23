using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Benches.Tier1;

public class TlvBenchmarks : Tier1Benchmark
{
    WalRecord _record8 = null!;
    WalRecord _record64 = null!;
    WalRecord _record256 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _record8 = Record(8);
        _record64 = Record(64);
        _record256 = Record(256);
    }

    [Benchmark]
    public byte[] EncodeField8B() => WalCodec.EncodeRecord(_record8);

    [Benchmark]
    public byte[] EncodeField64B() => WalCodec.EncodeRecord(_record64);

    [Benchmark]
    public byte[] EncodeField256B() => WalCodec.EncodeRecord(_record256);

    static WalRecord Record(int valueSize) => new(
        1,
        WalOperation.Put,
        BenchmarkData.Key(1),
        BenchmarkData.Value(valueSize),
        1,
        null,
        null,
        null,
        1);
}
