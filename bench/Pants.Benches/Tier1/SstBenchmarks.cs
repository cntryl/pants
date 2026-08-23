using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Benches.Tier1;

public class SstBenchmarks : Tier1Benchmark
{
    IReadOnlyList<MidgeSstEntry> _entries = null!;
    byte[] _encoded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries = Enumerable.Range(0, 128)
            .Select(index => new MidgeSstEntry(
                BenchmarkData.Key(index),
                BenchmarkData.Value(128, checked((byte)index)),
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        _encoded = MidgeSstCodec.Encode(_entries, [], PantsPerformanceGoal.Latency);
    }

    [Benchmark(OperationsPerInvoke = 128)]
    public byte[] Encode128() => MidgeSstCodec.Encode(_entries, [], PantsPerformanceGoal.Latency);

    [Benchmark(OperationsPerInvoke = 128)]
    public object Decode128() => MidgeSstCodec.Decode(_encoded);
}
