using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Benches.Tier2;

public class IteratorMultiSstSubsystemBenchmarks : Tier2Benchmark
{
    const int KeysPerRun = 1_000;
    SortedDictionary<byte[], CellState>[] _runs = null!;

    [GlobalSetup]
    public void Setup() => _runs = Enumerable.Range(0, 8).Select(CreateRun).ToArray();

    [Benchmark(OperationsPerInvoke = 8 * KeysPerRun)]
    public int MergeEightDisjointRuns()
    {
        var count = 0;
        foreach (var _ in _runs.SelectMany(run => run).OrderBy(entry => entry.Key, ByteArrayComparer.Instance))
        {
            count++;
        }

        return count;
    }

    static SortedDictionary<byte[], CellState> CreateRun(int run)
    {
        var entries = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
        for (var index = 0; index < KeysPerRun; index++)
        {
            entries.Add(
                Tier2Data.Key((run * KeysPerRun) + index),
                new CellState(Tier2Data.Value(64), index, null));
        }

        return entries;
    }
}
