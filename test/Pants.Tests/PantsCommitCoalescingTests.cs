namespace Pants.Tests;

public sealed class PantsCommitCoalescingTests
{
    [Fact]
    public async Task ShouldFanOutOneDurableSyncGivenConcurrentCommitsAndRecoverAll()
    {
        using var directory = new TemporaryDirectory();
        PantsRuntimeMetrics metrics;
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithBackgroundCompaction(false)))
        {
            var transactions = new List<IPantsTransaction>();
            for (int index = 0; index < 32; index++)
            {
                IPantsTransaction transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"key-{index:D2}"),
                    TestBytes.FromString($"value-{index:D2}"));
                transactions.Add(transaction);
            }

            await Task.WhenAll(transactions.Select(transaction => Task.Run(async () =>
            {
                await transaction.CommitAsync(PantsWriteOptions.Sync);
                await transaction.DisposeAsync();
            })));
            metrics = await database.GetRuntimeMetricsAsync();
        }

        Assert.Equal(32, metrics.WalAppendCount);
        Assert.InRange(metrics.WalFsyncCount, 1, 31);
        Assert.True(metrics.DurabilityWaitersFannedOutTotal > 1);

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using IPantsTransaction reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (int index = 0; index < 32; index++)
        {
            ReadOnlyMemory<byte>? value = await reader.GetAsync(
                TestBytes.FromString($"key-{index:D2}"));
            Assert.NotNull(value);
            Assert.Equal($"value-{index:D2}", TestBytes.ToText(value.Value));
        }
    }
}
