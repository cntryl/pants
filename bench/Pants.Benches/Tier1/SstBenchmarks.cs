using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Benches.Tier1;

public class SstBenchmarks : Tier1Benchmark
{
    IReadOnlyList<SstEntry> _entries = null!;
    byte[] _encoded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(
                BenchmarkData.Key(index),
                BenchmarkData.Value(128, checked((byte)index)),
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        _encoded = SstCodec.Encode(_entries, [], PantsPerformanceGoal.Latency);
    }

    [Benchmark(OperationsPerInvoke = 128)]
    public byte[] Encode128() => SstCodec.Encode(_entries, [], PantsPerformanceGoal.Latency);

    [Benchmark(OperationsPerInvoke = 128)]
    public object Decode128() => SstCodec.Decode(_encoded);
}
