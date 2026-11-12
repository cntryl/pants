using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsCommitSequenceAndCloudWaitTests
{
    [Fact]
    public async Task ShouldReturnEachAssignedCommitSequenceAcrossConcurrentWalGroupsAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var transactions = new List<IPantsTransaction>();
            try
            {
                for (var index = 0; index < 32; index++)
                {
                    var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                    transaction.Put(TestBytes.FromString($"key-{index}"), TestBytes.FromString($"value-{index}"));
                    transactions.Add(transaction);
                }
                var sequences = await Task.WhenAll(transactions.Select(transaction => transaction.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask()));
                Assert.All(sequences, static sequence => Assert.True(sequence > 0));
                Assert.Equal(sequences.Length, sequences.Distinct().Count());
                Assert.Equal((await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence, sequences.Max());
            }
            finally
            {
                foreach (var transaction in transactions)
                {
                    await transaction.DisposeAsync();
                }
            }
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal(TestBytes.FromString($"value-{index}"), (await reader.GetAsync(TestBytes.FromString($"key-{index}")))?.ToArray());
        }
    }

    [Fact]
    public async Task ShouldReturnServerCutForAssertionOnlyAndStartCutForPureEmptyAndReadOnly()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(database, "stable", "initial", PantsWriteOptions.Sync);
        var start = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        await using var assertion = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        assertion.AssertValue("stable"u8.ToArray(), "initial"u8.ToArray());
        await using var empty = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        await using var readOnly = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await PutAsync(database, "other", "later", PantsWriteOptions.Sync);
        var server = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        Assert.True(server > start);

        Assert.Equal(server, await assertion.CommitWithSequenceAsync(PantsWriteOptions.Sync));
        Assert.Equal(start, await empty.CommitWithSequenceAsync(PantsWriteOptions.Buffered));
        Assert.Equal(start, await readOnly.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
        Assert.Equal(server, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    [Fact]
    public async Task ShouldKeepPureEmptyCloudStrictAtItsStartCutAndConfirmAssertionOnlyAtTheServerCut()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "no-op-receipts/")
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue)));
        await PutAsync(database, "stable", "initial", PantsWriteOptions.CloudStrict);
        var start = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        await using var empty = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        await using var assertion = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        assertion.AssertValue("stable"u8.ToArray(), "initial"u8.ToArray());
        await PutAsync(database, "other", "pending", PantsWriteOptions.CloudAsync);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(before.CurrentSequence > start);
        Assert.True(before.WalCloudDurableSequence < before.CurrentSequence);

        Assert.Equal(start, await empty.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
        var afterEmpty = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalCloudDurableSequence, afterEmpty.WalCloudDurableSequence);
        Assert.Equal(before.WalAppendCount, afterEmpty.WalAppendCount);
        Assert.Equal(before.CurrentSequence, await assertion.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
        var afterAssertion = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, afterAssertion.CurrentSequence);
        Assert.Equal(before.WalAppendCount, afterAssertion.WalAppendCount);
        Assert.True(afterAssertion.WalCloudDurableSequence >= before.CurrentSequence);
    }

    [Fact]
    public async Task ShouldSealAndConfirmTheActualCloudAsyncTargetWithoutAnotherTransaction()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "durable-target/")
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "key", "value", PantsWriteOptions.CloudAsync);
            var sequence = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
            Assert.True(await database.Cloud!.GetDurableSequenceAsync() < sequence);
            Assert.True(await database.Cloud.WaitForDurableAsync(sequence, TestTimeouts.Expected));
            Assert.True(await database.Cloud.GetDurableSequenceAsync() >= sequence);
            Assert.Equal(sequence, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    static async Task PutAsync(IPantsDatabase database, string key, string value, PantsWriteOptions options)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(options);
    }
}
