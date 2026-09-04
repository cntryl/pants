using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier4;

public abstract class YcsbSystemBenchmark : Tier4Benchmark
{
    public const int InitialKeyCount = 50_000;
    public const int OperationCount = 1_000;
    const int BatchSize = 1_000;
    IPantsDatabase _database = null!;
    string _path = null!;
    byte[] _value = null!;

    protected abstract YcsbWorkload Workload { get; }

    public abstract IEnumerable<YcsbScenario> Scenarios { get; }

    [ParamsSource(nameof(Scenarios))] public YcsbScenario Scenario { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-ycsb-{Workload}-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier4Database.Options(_path, Scenario.StorageMode));
        _value = Tier4Data.Value(256);
        for (var start = 0; start < InitialKeyCount; start += BatchSize)
        {
            var entries = Enumerable.Range(start, BatchSize).Select(index => (Tier4Data.Key(index), _value));
            await Tier4Database.PutBatchAsync(
                _database,
                entries,
                Tier4Database.WriteOptions(Scenario.StorageMode));
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier4Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = OperationCount)]
    public async Task<int> RunAsync()
    {
        var nextOperation = -1;
        var completed = 0;
        var tasks = Enumerable.Range(0, Scenario.Clients).Select(client => Task.Run(async () =>
        {
            var localCompleted = 0;
            while (true)
            {
                var operation = Interlocked.Increment(ref nextOperation);
                if (operation >= OperationCount)
                {
                    break;
                }

                await ExecuteAsync(operation, client);
                localCompleted++;
            }

            Interlocked.Add(ref completed, localCompleted);
        }));
        await Task.WhenAll(tasks);
        if (completed != OperationCount)
        {
            throw new InvalidOperationException($"Completed {completed} operations; expected {OperationCount}.");
        }

        return completed;
    }

    async ValueTask ExecuteAsync(int operation, int client)
    {
        var selector = (operation * 17 + client * 31) % 100;
        var keyIndex = KeyIndex(operation, client);
        switch (Workload)
        {
            case YcsbWorkload.A:
                await ReadOrUpdateAsync(selector < 50, keyIndex);
                break;
            case YcsbWorkload.B:
                await ReadOrUpdateAsync(selector < 95, keyIndex);
                break;
            case YcsbWorkload.C:
                await Tier4Database.GetAsync(_database, Tier4Data.Key(keyIndex));
                break;
            case YcsbWorkload.D:
                if (selector < 95)
                {
                    await Tier4Database.GetAsync(_database, Tier4Data.Key(InitialKeyCount - 1 - keyIndex % 1_000));
                }
                else
                {
                    await PutAsync(InitialKeyCount + operation);
                }

                break;
            case YcsbWorkload.E:
                if (selector < 95)
                {
                    await ScanAsync(keyIndex);
                }
                else
                {
                    await PutAsync(InitialKeyCount + operation);
                }

                break;
            case YcsbWorkload.F:
                await Tier4Database.GetAsync(_database, Tier4Data.Key(keyIndex));
                await PutAsync(keyIndex);
                break;
            default:
                throw new InvalidOperationException($"Unsupported YCSB workload {Workload}.");
        }
    }

    static int KeyIndex(int operation, int client)
    {
        var uniformlyDistributed = ((long)operation * 7919 + (long)client * 104729) % InitialKeyCount;
        return checked((int)(uniformlyDistributed * uniformlyDistributed / InitialKeyCount));
    }

    async ValueTask ReadOrUpdateAsync(bool read, int keyIndex)
    {
        if (read)
        {
            await Tier4Database.GetAsync(_database, Tier4Data.Key(keyIndex));
        }
        else
        {
            await PutAsync(keyIndex);
        }
    }

    ValueTask PutAsync(int keyIndex) => Tier4Database.PutBatchAsync(
        _database,
        [(Tier4Data.Key(keyIndex), _value)],
        Tier4Database.WriteOptions(Scenario.StorageMode));

    async ValueTask ScanAsync(int keyIndex)
    {
        await using var transaction = await _database.Transactions.BeginAsync(
            _database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = Tier4Data.Key(keyIndex),
            Limit = 64
        });
        await foreach (var _ in scan)
        {
        }
    }
}
