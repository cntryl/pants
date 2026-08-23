using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier3;

public class SstSystemBenchmarks : Tier3Benchmark
{
    const int KeyCount = 4_096;
    const int RangeBatchSize = 64;
    string _path = null!;
    IPantsDatabase _database = null!;
    IPantsTransaction _snapshot = null!;
    byte[][] _keys = null!;
    int _nextKey;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-sst-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode));
        _keys = Enumerable.Range(0, KeyCount).Select(index => Tier3Data.Key(index)).ToArray();
        await Tier3Database.PutBatchAsync(
            _database,
            _keys.Select((key, index) => (key, Tier3Data.Value(64, index))),
            Tier3Database.WriteOptions(StorageMode));
        await _database.FlushAsync(_database.DefaultColumnFamily);
        _snapshot = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _snapshot.DisposeAsync();
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
    }

    [Benchmark]
    public ValueTask<ReadOnlyMemory<byte>?> PointSeekAsync()
    {
        var index = unchecked((uint)Interlocked.Increment(ref _nextKey) % KeyCount);
        return _snapshot.GetAsync(_keys[index]);
    }

    [Benchmark(OperationsPerInvoke = RangeBatchSize)]
    public async Task<int> RangeSeekFirstRowsAsync()
    {
        var validated = 0;
        for (var offset = 0; offset < RangeBatchSize; offset++)
        {
            var index = unchecked((uint)Interlocked.Increment(ref _nextKey) % KeyCount);
            await using var scan = await _snapshot.ScanAsync(new PantsScanQuery
            {
                StartInclusive = _keys[index],
                Limit = 1
            });
            await foreach (var entry in scan)
            {
                if (entry.Key.Span.SequenceEqual(_keys[index]))
                {
                    validated++;
                }

                break;
            }
        }

        return validated;
    }
}
