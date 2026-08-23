using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Benches.Tier3;

public class EngineSystemBenchmarks : Tier3Benchmark
{
    const int KeyCount = 4_096;
    string _path = null!;
    IPantsDatabase _database = null!;
    byte[][] _keys = null!;
    int _nextKey;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-engine-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode));
        _keys = Enumerable.Range(0, KeyCount).Select(index => Tier3Data.Key(index)).ToArray();
        await Tier3Database.PutBatchAsync(
            _database,
            _keys.Select((key, index) => (key, Tier3Data.Value(64, index))),
            Tier3Database.WriteOptions(StorageMode));
        await _database.FlushAsync(_database.DefaultColumnFamily);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
    }

    [Benchmark]
    public ValueTask<ReadOnlyMemory<byte>?> GetAsync()
    {
        var index = unchecked((uint)Interlocked.Increment(ref _nextKey) % KeyCount);
        return Tier3Database.GetAsync(_database, _keys[index]);
    }
}
