namespace Cntryl.Pants.Tests.Observability;

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
        await using (var write = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            write.Put("key"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var baseline = await database.GetReadAmplificationMetricsAsync();
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = Enumerable.Range(0, operationCount)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadOnly);
                Assert.NotNull(await transaction.GetAsync("key"u8.ToArray()));
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(reads).WaitAsync(AssertionTimeout);
        var after = await database.GetReadAmplificationMetricsAsync();

        Assert.Equal(operationCount, after.ReadsTotal - baseline.ReadsTotal);
    }
}
