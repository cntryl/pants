using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeDurabilitySyncCountParityTests
{
    [Fact]
    public async Task ShouldKeepBufferedGenerationHealthyAfterEmptySyncBarrier()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await using (var empty = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            await empty.CommitAsync(PantsWriteOptions.Sync);
        }

        var committedSequence = await CommitAsync(
            database,
            "after-empty-sync",
            "durable-value",
            PantsWriteOptions.Buffered);
        await WaitForLocalDurabilityAsync(database, committedSequence);
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(committedSequence, after.WalLastSyncedSequence);
        Assert.Equal(committedSequence, after.WalLocalDurableSequence);
        Assert.Equal(PantsEngineHealth.Healthy, after.Health);
        Assert.Equal(before.DurabilityWaitersFannedOutTotal, after.DurabilityWaitersFannedOutTotal);
        Assert.Equal(before.RuntimeLateResponsesTotal, after.RuntimeLateResponsesTotal);
        await AssertValueAsync(database, "after-empty-sync", "durable-value");
    }

    [Fact]
    public async Task ShouldKeepBufferedGenerationHealthyAfterAssertionOnlySyncBarrier()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        _ = await CommitAsync(database, "assertion-guard", "value", PantsWriteOptions.Buffered);
        await using (var assertion = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            assertion.AssertValue(
                TestBytes.FromString("assertion-guard"),
                TestBytes.FromString("value"));
            await assertion.CommitAsync(PantsWriteOptions.Sync);
        }

        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        var committedSequence = await CommitAsync(
            database,
            "after-assertion-sync",
            "durable-value",
            PantsWriteOptions.Buffered);
        await WaitForLocalDurabilityAsync(database, committedSequence);
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(committedSequence, after.WalLastSyncedSequence);
        Assert.Equal(committedSequence, after.WalLocalDurableSequence);
        Assert.Equal(PantsEngineHealth.Healthy, after.Health);
        Assert.Equal(before.DurabilityWaitersFannedOutTotal, after.DurabilityWaitersFannedOutTotal);
        Assert.Equal(before.RuntimeLateResponsesTotal, after.RuntimeLateResponsesTotal);
        await AssertValueAsync(database, "assertion-guard", "value");
        await AssertValueAsync(database, "after-assertion-sync", "durable-value");
    }

    static async Task<long> CommitAsync(
        IPantsDatabase database,
        string key,
        string value,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(writeOptions);
        return (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
    }

    static async Task AssertValueAsync(IPantsDatabase database, string key, string expected)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            expected,
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString(key)))));
    }

    static async Task WaitForLocalDurabilityAsync(IPantsDatabase database, long sequence)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while ((await database.Diagnostics.GetRuntimeMetricsAsync()).WalLocalDurableSequence < sequence)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
