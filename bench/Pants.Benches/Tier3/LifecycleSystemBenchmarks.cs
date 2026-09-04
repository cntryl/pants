using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier3;

public class LifecycleSystemBenchmarks : Tier3Benchmark
{
    const int FlushBatchSize = 32;
    int _batch;
    IPantsDatabase _database = null!;
    string _path = null!;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-lifecycle-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
    }

    [Benchmark]
    public async Task WriteAndFlushCycleAsync()
    {
        var batch = Interlocked.Increment(ref _batch);
        var entries = Enumerable.Range(0, FlushBatchSize).Select(offset =>
            (Tier3Data.Key(batch * FlushBatchSize + offset), Tier3Data.Value(64, offset)));
        await Tier3Database.PutBatchAsync(_database, entries, Tier3Database.WriteOptions(StorageMode));
        await _database.Maintenance.FlushAsync(_database.ColumnFamilies.DefaultFamily);
    }

    [Benchmark]
    public async Task CleanReopenAsync()
    {
        await _database.ShutdownAsync(TimeSpan.FromSeconds(10));
        await _database.DisposeAsync();
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode));
    }
}
