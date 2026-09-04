using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier4;

public class StreamingSystemBenchmarks : Tier4Benchmark
{
    const int OperationCount = 20_000;
    IPantsDatabase _database = null!;
    int _nextOperation;
    string _path = null!;

    [Params(Tier4StorageMode.Local, Tier4StorageMode.SimulatedCloud)]
    public Tier4StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-streaming-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier4Database.Options(_path, StorageMode));
        await Tier4Database.PutBatchAsync(
            _database,
            Enumerable.Range(0, 1_000).Select(index => (Tier4Data.Key(index), Tier4Data.Value(256, index))),
            Tier4Database.WriteOptions(StorageMode));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier4Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = OperationCount)]
    public async Task MixedReadersAndWritersAsync()
    {
        _nextOperation = -1;
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            while (true)
            {
                var operation = Interlocked.Increment(ref _nextOperation);
                if (operation >= OperationCount)
                {
                    break;
                }

                var key = operation % 1_000;
                if (worker < 2)
                {
                    await Tier4Database.PutBatchAsync(
                        _database,
                        [(Tier4Data.Key(key), Tier4Data.Value(256, operation))],
                        Tier4Database.WriteOptions(StorageMode));
                }
                else
                {
                    await Tier4Database.GetAsync(_database, Tier4Data.Key(key));
                }
            }
        }));
        await Task.WhenAll(workers);
    }
}
