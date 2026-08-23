using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier4;

public class StrictGroupCommitSystemBenchmarks : Tier4Benchmark
{
    const int Writers = 16;
    const int FlushWaves = 4;
    const int TransactionsPerWriterPerWave = 16;
    const int TotalTransactions = Writers * FlushWaves * TransactionsPerWriterPerWave;
    string _path = null!;

    [IterationSetup]
    public void Setup() => _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-strict-{Guid.NewGuid():N}");

    [IterationCleanup]
    public void Cleanup() => Tier4Database.DeletePath(_path);

    [Benchmark(OperationsPerInvoke = TotalTransactions)]
    public async Task<int> CompleteLocalSystemAsync()
    {
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(_path).WithMemtableLimits(128 * 1024)))
        {
            for (var wave = 0; wave < FlushWaves; wave++)
            {
                var capturedWave = wave;
                var tasks = Enumerable.Range(0, Writers).Select(writer => CommitWriterAsync(database, capturedWave, writer));
                await Task.WhenAll(tasks);
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            await database.CompactAllAsync();
            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(_path));
        var count = 0;
        {
            await using var transaction = await reopened.BeginTransactionAsync(
                reopened.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            await using var scan = await transaction.ScanAsync(new PantsScanQuery());
            await foreach (var _ in scan)
            {
                count++;
            }
        }

        if (count != TotalTransactions)
        {
            throw new InvalidOperationException($"Reopened database contained {count} records; expected {TotalTransactions}.");
        }

        await reopened.ShutdownAsync(TimeSpan.FromSeconds(10));
        return count;
    }

    static async Task CommitWriterAsync(IPantsDatabase database, int wave, int writer)
    {
        for (var transactionIndex = 0; transactionIndex < TransactionsPerWriterPerWave; transactionIndex++)
        {
            var ordinal = ((wave * Writers + writer) * TransactionsPerWriterPerWave) + transactionIndex;
            await Tier4Database.PutBatchAsync(
                database,
                [(Tier4Data.Key(ordinal), Tier4Data.Value(256, ordinal))],
                PantsWriteOptions.Sync);
        }
    }
}
