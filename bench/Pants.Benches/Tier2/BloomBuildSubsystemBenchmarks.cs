using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Benches.Tier2;

public class BloomBuildSubsystemBenchmarks : Tier2Benchmark
{
    SstEntry[] _tenThousand = null!;
    SstEntry[] _hundredThousand = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tenThousand = Entries(10_000);
        _hundredThousand = Entries(100_000);
    }

    [Benchmark(OperationsPerInvoke = 10_000)]
    public byte[] Build10K() => SstCodec.Encode(_tenThousand, [], PantsPerformanceGoal.Latency);

    [Benchmark(OperationsPerInvoke = 100_000)]
    public byte[] Build100K() => SstCodec.Encode(_hundredThousand, [], PantsPerformanceGoal.Latency);

    static SstEntry[] Entries(int count) => Enumerable.Range(0, count)
        .Select(index => new SstEntry(Tier2Data.Key(index), Tier2Data.Value(64), checked((ulong)index + 1), null, false))
        .ToArray();
}
