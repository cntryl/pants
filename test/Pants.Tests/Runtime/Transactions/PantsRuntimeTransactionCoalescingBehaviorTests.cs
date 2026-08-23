using System.Globalization;

namespace Cntryl.Pants.Tests.Runtime.Transactions;

public sealed class PantsRuntimeTransactionCoalescingBehaviorTests
{
    [Fact]
    public async Task ShouldPreserveEveryConcurrentWriteWhileRuntimeCoalesces()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        const int commitCount = 16;
        var transactions = new List<IPantsTransaction>(commitCount);
        for (var index = 0; index < commitCount; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"coalesced-key-{index:D2}"),
                TestBytes.FromString($"coalesced-value-{index:D2}"));
            transactions.Add(transaction);
        }

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TimeSpan.FromSeconds(5));
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(5));

        var metrics = await database.GetRuntimeMetricsAsync();
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var rowCount = 0;
        await foreach (var _ in scan)
        {
            rowCount++;
        }

        Assert.Equal(commitCount, rowCount);
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.Equal(0, metrics.WalFsyncCount);
        Assert.Equal(0, metrics.DurabilityWaitersFannedOutTotal);

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldMaintainOrderingAcrossSequentialRuntimeCommits()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.CreateColumnFamilyAsync("test_counter");
        const int operationCount = 100;
        for (var index = 0; index < operationCount; index++)
        {
            await CommitAsync(database, family, "counter", index.ToString(
                CultureInfo.InvariantCulture));
        }

        await using var reader = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var finalValue = await reader.GetAsync("counter"u8.ToArray());

        Assert.Equal("99", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(finalValue)));
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }
}
