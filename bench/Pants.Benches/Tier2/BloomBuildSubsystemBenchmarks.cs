using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Benches.Tier2;

public class BloomBuildSubsystemBenchmarks : Tier2Benchmark
{
    MidgeSstEntry[] _tenThousand = null!;
    MidgeSstEntry[] _hundredThousand = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tenThousand = Entries(10_000);
        _hundredThousand = Entries(100_000);
    }

    [Benchmark(OperationsPerInvoke = 10_000)]
    public byte[] Build10K() => MidgeSstCodec.Encode(_tenThousand, [], PantsPerformanceGoal.Latency);

    [Benchmark(OperationsPerInvoke = 100_000)]
    public byte[] Build100K() => MidgeSstCodec.Encode(_hundredThousand, [], PantsPerformanceGoal.Latency);

    static MidgeSstEntry[] Entries(int count) => Enumerable.Range(0, count)
        .Select(index => new MidgeSstEntry(Tier2Data.Key(index), Tier2Data.Value(64), checked((ulong)index + 1), null, false))
        .ToArray();
}
