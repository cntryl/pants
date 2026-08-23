using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Benches.Tier1;

public class MemtableBenchmarks : Tier1Benchmark
{
    readonly byte[] _hitKey = BenchmarkData.Key(500);
    readonly byte[] _missKey = BenchmarkData.Key(2_000);
    readonly byte[] _value = BenchmarkData.Value(64);
    SortedDictionary<byte[], CellState> _memtable = null!;
    byte[] _putKey = null!;

    [GlobalSetup]
    public void Setup()
    {
        _memtable = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
        for (var index = 0; index < 1_000; index++)
        {
            _memtable.Add(BenchmarkData.Key(index), new CellState(_value, index, null));
        }

        _putKey = BenchmarkData.Key(10_000);
    }

    [Benchmark]
    public bool GetHit() => _memtable.TryGetValue(_hitKey, out _);

    [Benchmark]
    public bool GetMiss() => _memtable.TryGetValue(_missKey, out _);

    [Benchmark]
    public void Put() => _memtable[_putKey] = new CellState(_value, 10_000, null);

    [Benchmark(OperationsPerInvoke = 100)]
    public int Iterate100()
    {
        var count = 0;
        foreach (var _ in _memtable.Take(100))
        {
            count++;
        }

        return count;
    }
}
