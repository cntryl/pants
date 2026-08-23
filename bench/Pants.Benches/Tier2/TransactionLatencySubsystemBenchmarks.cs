using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier2;

public class TransactionLatencySubsystemBenchmarks : Tier2Benchmark
{
    const int TransactionCount = 1_024;
    const int ConcurrentClients = 16;
    IPantsDatabase _database = null!;
    byte[][] _keys = null!;
    byte[] _value = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        _keys = Enumerable.Range(0, TransactionCount).Select(Tier2Data.Key).ToArray();
        _value = Tier2Data.Value(64);
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _database.DisposeAsync();

    [Benchmark(OperationsPerInvoke = TransactionCount)]
    public async Task SequentialCommitLatencyAsync()
    {
        foreach (var key in _keys)
        {
            await Tier2Database.PutAsync(_database, key, _value, PantsWriteOptions.BestEffort);
        }
    }

    [Benchmark(OperationsPerInvoke = TransactionCount)]
    public async Task ReadOnlyBeginLatencyAsync()
    {
        for (var index = 0; index < TransactionCount; index++)
        {
            await using var transaction = await _database.BeginTransactionAsync(
                _database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
        }
    }

    [Benchmark(OperationsPerInvoke = TransactionCount)]
    public async Task CoalescingSignalAsync()
    {
        var tasks = Enumerable.Range(0, ConcurrentClients).Select(client => CommitClientAsync(client));
        await Task.WhenAll(tasks);
    }

    async Task CommitClientAsync(int client)
    {
        for (var index = client; index < TransactionCount; index += ConcurrentClients)
        {
            await Tier2Database.PutAsync(_database, _keys[index], _value, PantsWriteOptions.BestEffort);
        }
    }
}
