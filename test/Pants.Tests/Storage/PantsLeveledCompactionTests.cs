using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsLeveledCompactionTests
{
    [Fact]
    public async Task ShouldRunBackgroundCompactionAtL0Trigger()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithPerformanceGoal(PantsPerformanceGoal.Latency)
                .WithBackgroundCompaction(true));
        for (var index = 0; index < 3; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        var level = Assert.Single(layout.Levels);
        Assert.Equal(1, level.Level);
        Assert.Equal(1, level.FileCount);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.CompactionsRun);
        Assert.True(metrics.CompactionBytesRewritten > 0);
    }

    [Fact]
    public async Task ShouldKeepCompactionInputSetBoundedAndReportMultipleLevels()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3)));
        for (var index = 0; index < 8; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal([0, 1], layout.Levels.Select(static level => level.Level));
        Assert.Equal(2, layout.Levels.Single(static level => level.Level == 0).FileCount);
        Assert.Equal(2, layout.Levels.Single(static level => level.Level == 1).FileCount);
    }

    [Fact]
    public async Task ShouldChangeBackgroundCompactionAtRuntime()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3)));
        for (var index = 0; index < 3; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        Assert.Equal(3, Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).FileCount);

        await database.Maintenance.SetBackgroundCompactionAsync(true);
        await PutAndFlushAsync(database, 3);

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Contains(layout.Levels, static level => level.Level == 1);
    }

    [Fact]
    public async Task ShouldPublishNoOutputWhenCompactionProvesADeleteObsolete()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Delete(TestBytes.FromString("key"));
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.CompactAllAsync();
            Assert.Empty((await database.Diagnostics.GetStorageLayoutAsync()).Levels);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await read.GetAsync(TestBytes.FromString("key")));
    }

    [Fact]
    public async Task ShouldPublishMultipleTargetSizedOutputsWithUniqueSequences()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                TargetSstSizeBytes: 80));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var batch = 0; batch < 2; batch++)
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                for (var index = 0; index < 5; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"key-{batch}-{index}"),
                        TestBytes.FromString(new string('v', 40)));
                }

                await transaction.CommitAsync(PantsWriteOptions.Buffered);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }

            await database.Maintenance.CompactAllAsync();
            var level = Assert.Single(
                (await database.Diagnostics.GetStorageLayoutAsync()).Levels,
                static candidate => candidate.Level == 1);
            Assert.True(level.FileCount > 1);
            Assert.Equal(level.FileCount, level.Files.Select(static file => file.Name).Distinct().Count());
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var entries = new List<PantsEntry>();
        await using var scan = await read.ScanAsync(new PantsScanQuery());
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        Assert.Equal(10, entries.Count);
    }

    static async ValueTask PutAndFlushAsync(IPantsDatabase database, int index)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(
            TestBytes.FromString($"key-{index:D4}"),
            TestBytes.FromString($"value-{index:D4}"));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }
}
