using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Benches.Tier4;

public class RecoveryThroughputSystemBenchmarks : Tier4Benchmark
{
    const int KeyCount = 1_000;
    string _path = null!;

    [Params(Tier4StorageMode.Local, Tier4StorageMode.SimulatedCloud)]
    public Tier4StorageMode StorageMode { get; set; }

    [ParamsAllValues]
    public RecoveryState State { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-recovery-{Guid.NewGuid():N}");
        await using var database = await PantsDatabase.OpenAsync(Tier4Database.Options(_path, StorageMode));
        await Tier4Database.PutBatchAsync(
            database,
            Enumerable.Range(0, KeyCount).Select(index => (Tier4Data.Key(index), Tier4Data.Value(64, index))),
            Tier4Database.WriteOptions(StorageMode));
        await database.FlushAsync(database.DefaultColumnFamily);
        if (State == RecoveryState.Compacted)
        {
            await database.CompactAllAsync();
        }

        await database.ShutdownAsync(TimeSpan.FromSeconds(10));
    }

    [GlobalCleanup]
    public void Cleanup() => Tier4Database.DeletePath(_path);

    [Benchmark(OperationsPerInvoke = 100)]
    public async Task ReopenAndRead100Async()
    {
        await using var database = await PantsDatabase.OpenAsync(Tier4Database.Options(_path, StorageMode));
        for (var index = 0; index < 100; index++)
        {
            if (await Tier4Database.GetAsync(database, Tier4Data.Key(index)) is null)
            {
                throw new InvalidOperationException($"Recovered key {index} was not readable.");
            }
        }

        await database.ShutdownAsync(TimeSpan.FromSeconds(10));
    }
}
