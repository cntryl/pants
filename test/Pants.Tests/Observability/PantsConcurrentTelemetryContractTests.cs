using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsConcurrentTelemetryContractTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ShouldCountEveryReadExactlyUnderConcurrentContention()
    {
        const int operationCount = 32;
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (var write = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            write.Put("key"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var baseline = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = Enumerable.Range(0, operationCount)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using var transaction = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadOnly);
                Assert.NotNull(await transaction.GetAsync("key"u8.ToArray()));
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(reads).WaitAsync(AssertionTimeout);
        var after = await database.Diagnostics.GetReadAmplificationMetricsAsync();

        Assert.Equal(operationCount, after.ReadsTotal - baseline.ReadsTotal);
    }
}
