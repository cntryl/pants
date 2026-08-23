using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Benches.Tier1;

public class WalBenchmarks : Tier1Benchmark
{
    MidgeWalRecord _delete = null!;
    MidgeWalRecord _mediumPut = null!;
    MidgeWalRecord _smallPut = null!;
    byte[] _encodedMediumPut = null!;
    byte[] _encodedSmallPut = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallPut = Record(MidgeWalOperation.Put, BenchmarkData.Value(64));
        _mediumPut = Record(MidgeWalOperation.Put, BenchmarkData.Value(4 * 1024));
        _delete = Record(MidgeWalOperation.Delete, null);
        _encodedSmallPut = MidgeWalCodec.EncodeRecord(_smallPut);
        _encodedMediumPut = MidgeWalCodec.EncodeRecord(_mediumPut);
    }

    [Benchmark]
    public byte[] EncodeSmallPut() => MidgeWalCodec.EncodeRecord(_smallPut);

    [Benchmark]
    public byte[] EncodeMediumPut() => MidgeWalCodec.EncodeRecord(_mediumPut);

    [Benchmark]
    public byte[] EncodeDelete() => MidgeWalCodec.EncodeRecord(_delete);

    [Benchmark]
    public object DecodeSmallPut() => MidgeWalCodec.DecodeRecord(_encodedSmallPut);

    [Benchmark]
    public object DecodeMediumPut() => MidgeWalCodec.DecodeRecord(_encodedMediumPut);

    static MidgeWalRecord Record(MidgeWalOperation operation, byte[]? value) => new(
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
