using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Tier1;

public class IteratorBenchmarks : Tier1Benchmark
{
    SortedDictionary<byte[], CellState> _entries = null!;
    byte[] _middle = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
        for (var index = 0; index < 1_000; index++)
        {
            _entries.Add(BenchmarkData.Key(index), new CellState(BenchmarkData.Value(64), index, null));
        }

        _middle = BenchmarkData.Key(500);
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public int Sequential100()
    {
        var count = 0;
        foreach (var _ in _entries.Take(100))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public byte[] SeekMiddle() => _entries.First(entry =>
        ByteArrayComparer.Instance.Compare(entry.Key, _middle) >= 0).Key;
}
