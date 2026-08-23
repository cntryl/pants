using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Benches.Tier2;

public class MemtableRotateSubsystemBenchmarks : Tier2Benchmark
{
    const int EntryCount = 4_096;
    (byte[] Key, byte[] Value)[] _oneKilobyteEntries = null!;
    (byte[] Key, byte[] Value)[] _fourKilobyteEntries = null!;

    [GlobalSetup]
    public void Setup()
    {
        _oneKilobyteEntries = Entries(1_024);
        _fourKilobyteEntries = Entries(4_096);
    }

    [Benchmark(OperationsPerInvoke = EntryCount)]
    public int Rotate1KValues() => Build(_oneKilobyteEntries);

    [Benchmark(OperationsPerInvoke = EntryCount)]
    public int Rotate4KValues() => Build(_fourKilobyteEntries);

    static (byte[] Key, byte[] Value)[] Entries(int valueSize) => Enumerable.Range(0, EntryCount)
        .Select(index => (Tier2Data.Key(index), Tier2Data.Value(valueSize, index)))
        .ToArray();

    static int Build((byte[] Key, byte[] Value)[] entries)
    {
        var memtable = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
        for (var index = 0; index < entries.Length; index++)
        {
            memtable[entries[index].Key] = new CellState(entries[index].Value, index + 1, null);
        }

        return memtable.Count;
    }
}
