using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier3;

public class MvccSystemBenchmarks : Tier3Benchmark
{
    const int KeyCount = 50_000;
    const int ReadBatchSize = 64;
    IPantsDatabase _database = null!;
    byte[][] _keys = null!;
    int _nextKey;
    string _path = null!;
    IPantsTransaction _snapshot = null!;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-mvcc-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode));
        _keys = Enumerable.Range(0, KeyCount).Select(index => Tier3Data.Key(index)).ToArray();
        await Tier3Database.PutBatchAsync(
            _database,
            _keys.Select(key => (key, Tier3Data.Value(64, 1))),
            Tier3Database.WriteOptions(StorageMode));
        _snapshot = await _database.Transactions.BeginAsync(
            _database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await Tier3Database.PutBatchAsync(
            _database,
            _keys.Select(key => (key, Tier3Data.Value(64, 2))),
            Tier3Database.WriteOptions(StorageMode));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _snapshot.DisposeAsync();
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = ReadBatchSize)]
    public async Task<int> ReadOldVersionAsync()
    {
        var validated = 0;
        for (var offset = 0; offset < ReadBatchSize; offset++)
        {
            var index = unchecked((uint)Interlocked.Increment(ref _nextKey) % KeyCount);
            var value = await _snapshot.GetAsync(_keys[index]);
            if (value is { } found && found.Span[0] == 1)
            {
                validated++;
            }
        }

        return validated;
    }

    [Benchmark]
    public async Task BeginReadOnlyTransactionAsync()
    {
        await using var transaction = await _database.Transactions.BeginAsync(
            _database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
    }
}
