using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsFlushPublicationMetricsTests
{
    [Fact]
    public async Task ShouldRecordOnlySuccessfulMatchingInstallationAcrossPublicationRetry()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new ManualTimeProvider();
        var failpoint = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeFlushManifestPublish);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoint, runtimeTimeProvider: timeProvider));
        try
        {
            await CommitAsync(database, database.ColumnFamilies.DefaultFamily);
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            var failed = await WaitForMetricsAsync(
                database,
                static metrics => metrics.FlushFailuresTotal > 0);

            Assert.Equal(0, failed.FlushPublishCount);
            Assert.Equal(0, failed.FlushPublishNanosecondsTotal);
            Assert.Equal(0, failed.FlushPublishNanosecondsMaximum);
            Assert.Equal(1, failed.ImmutableMemtables);
            Assert.Equal(0, failed.SstCount);

            timeProvider.Advance(TimeSpan.FromSeconds(3));
            failpoint.Release();
            var completed = await WaitForMetricsAsync(
                database,
                static metrics => metrics.ImmutableMemtables == 0);

            Assert.True(completed.FlushRetriesTotal > 0);
            Assert.Equal(1, completed.FlushPublishCount);
            Assert.Equal(3_000_000_000, completed.FlushPublishNanosecondsTotal);
            Assert.Equal(3_000_000_000, completed.FlushPublishNanosecondsMaximum);
            Assert.Equal(1, completed.SstCount);
            await AssertValueAsync(database);
        }
        finally
        {
            failpoint.Release();
        }

        await database.ShutdownAsync(TestTimeouts.Expected);
        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        await AssertValueAsync(reopened);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024 * 1024))
            .WithMemtableLimits(512 * 1024, 128 * 1024)
            .WithTransactionMemoryPool(512 * 1024)
            .WithBackgroundCompaction(false);

    static async Task CommitAsync(IPantsDatabase database, IPantsColumnFamily family)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put("row"u8.ToArray(), new byte[160 * 1024]);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task AssertValueAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync("row"u8.ToArray());
        Assert.Equal(new byte[160 * 1024], value?.ToArray());
    }

    static async Task<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> condition)
    {
        PantsRuntimeMetrics? metrics = null;
        await TestWait.UntilAsync(async () =>
        {
            metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            return condition(metrics);
        }, "the requested flush metrics are published");
        return metrics!;
    }
}
