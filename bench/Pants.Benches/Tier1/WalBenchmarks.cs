using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Tier1;

public class WalBenchmarks : Tier1Benchmark
{
    WalRecord _delete = null!;
    byte[] _encodedMediumPut = null!;
    byte[] _encodedSmallPut = null!;
    WalRecord _mediumPut = null!;
    WalRecord _smallPut = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallPut = Record(WalOperation.Put, BenchmarkData.Value(64));
        _mediumPut = Record(WalOperation.Put, BenchmarkData.Value(4 * 1024));
        _delete = Record(WalOperation.Delete, null);
        _encodedSmallPut = WalCodec.EncodeRecord(_smallPut);
        _encodedMediumPut = WalCodec.EncodeRecord(_mediumPut);
    }

    [Benchmark]
    public byte[] EncodeSmallPut() => WalCodec.EncodeRecord(_smallPut);

    [Benchmark]
    public byte[] EncodeMediumPut() => WalCodec.EncodeRecord(_mediumPut);

    [Benchmark]
    public byte[] EncodeDelete() => WalCodec.EncodeRecord(_delete);

    [Benchmark]
    public object DecodeSmallPut() => WalCodec.DecodeRecord(_encodedSmallPut);

    [Benchmark]
    public object DecodeMediumPut() => WalCodec.DecodeRecord(_encodedMediumPut);

    static WalRecord Record(WalOperation operation, byte[]? value) => new(
        1,
        operation,
        BenchmarkData.Key(1),
        value,
        1,
        null,
        null,
        null,
        1);
}
