using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier1;

public class ApiBenchmarks : Tier1Benchmark
{
    readonly byte[] _hitKey = BenchmarkData.Key(0);
    readonly byte[] _missKey = BenchmarkData.Key(int.MaxValue);
    readonly byte[] _putKey = BenchmarkData.Key(1);
    readonly byte[] _value = BenchmarkData.Value(64);
    IPantsDatabase _database = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(_hitKey, _value);
        await transaction.CommitAsync(PantsWriteOptions.BestEffort);
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _database.DisposeAsync();

    [Benchmark]
    public async ValueTask<ReadOnlyMemory<byte>?> GetHit()
    {
        await using var transaction = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(_hitKey);
    }

    [Benchmark]
    public async ValueTask<ReadOnlyMemory<byte>?> GetMiss()
    {
        await using var transaction = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(_missKey);
    }

    [Benchmark]
    public async ValueTask Put()
    {
        await using var transaction = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(_putKey, _value);
        await transaction.CommitAsync(PantsWriteOptions.BestEffort);
    }
}
