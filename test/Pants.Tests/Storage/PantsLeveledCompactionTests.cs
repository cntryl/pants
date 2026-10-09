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
    public async Task ShouldDrainAllL0DebtAfterCompactAllWellAboveL0Trigger()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3, BackgroundEnabled: false)));
        for (var index = 0; index < 8; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(layout.Levels, static level => level.Level == 0);
        Assert.Contains(layout.Levels, static level => level.Level == 1);
    }

    [Fact]
    public async Task ShouldLeaveNoL0FileAfterCompactAllForSingleFlush()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3, BackgroundEnabled: false)));
        await PutAndFlushAsync(database, 0);

        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(layout.Levels, static level => level.Level == 0);
        Assert.Contains(layout.Levels, static level => level.Level == 1);
    }

    [Fact]
    public async Task ShouldNotRewriteUnderTargetInnerLevelsWhenCompactAllFindsNoL0Debt()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false)));
        await PutAndFlushAsync(database, 0);
        await PutAndFlushAsync(database, 1);
        await database.Maintenance.CompactAllAsync();
        await PutAndFlushAsync(database, 2);
        await PutAndFlushAsync(database, 3);
        await database.Maintenance.CompactAllAsync();
        var before = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal(2, before.Levels.Single(static level => level.Level == 1).FileCount);

        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal([1], layout.Levels.Select(static level => level.Level));
        Assert.Equal(2, layout.Levels.Single(static level => level.Level == 1).FileCount);
    }

    [Fact]
    public async Task ShouldDrainL0DebtWhenCompactAllStartsDuringBackgroundCompaction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(true)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: true)));
        for (var index = 0; index < 5; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        var compaction = database.Maintenance.CompactAllAsync().AsTask();
        await PutAndFlushAsync(database, 5);
        await compaction;
        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(layout.Levels, static level => level.Level == 0);
    }

    [Fact]
    public async Task ShouldChangeBackgroundCompactionAtRuntime()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3, BackgroundEnabled: false)));
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
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
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
                TargetSstSizeBytes: 80,
                BackgroundEnabled: false));
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

    [Fact]
    public async Task ShouldReadEveryKeyAfterCompactionSplitsOutputsAcrossARangeTombstone()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                TargetSstSizeBytes: 80,
                BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var first = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 8; index++)
            {
                first.Put(
                    TestBytes.FromString($"key-{index}"),
                    TestBytes.FromString(new string('v', 40)));
            }

            await first.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using (var second = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            second.DeleteRange(TestBytes.FromString("key-2"), TestBytes.FromString("key-5"));
            second.Put("key-9"u8.ToArray(), TestBytes.FromString(new string('w', 40)));
            await second.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await database.Maintenance.CompactAllAsync();

        var level = Assert.Single(
            (await database.Diagnostics.GetStorageLayoutAsync()).Levels,
            static candidate => candidate.Level == 1);
        Assert.True(level.FileCount > 1);
        await using var read = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 8; index++)
        {
            var value = await read.GetAsync(TestBytes.FromString($"key-{index}"));
            Assert.Equal(index is >= 2 and < 5, value is null);
        }

        Assert.NotNull(await read.GetAsync("key-9"u8.ToArray()));
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
