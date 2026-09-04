using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier4;

public class LocalThroughputSystemBenchmarks : Tier4Benchmark
{
    const int BatchSize = 100;
    const int BatchCount = 1_000;
    (byte[] Key, byte[] Value)[][] _batches = null!;
    IPantsDatabase _database = null!;
    string _path = null!;

    [Params(Tier4StorageMode.Memory, Tier4StorageMode.Local)]
    public Tier4StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-throughput-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier4Database.Options(_path, StorageMode));
        _batches = Enumerable.Range(0, BatchCount).Select(batch => Enumerable.Range(0, BatchSize)
            .Select(offset =>
            {
                var index = batch * BatchSize + offset;
                return (Tier4Data.Key(index), Tier4Data.Value(128, index));
            }).ToArray()).ToArray();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier4Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = BatchSize * BatchCount)]
    public async Task BatchedWritesAsync()
    {
        foreach (var batch in _batches)
        {
            await Tier4Database.PutBatchAsync(_database, batch, Tier4Database.WriteOptions(StorageMode));
        }
    }
}
