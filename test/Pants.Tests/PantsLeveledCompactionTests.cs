namespace Pants.Tests;

public sealed class PantsLeveledCompactionTests
{
    [Fact]
    public async Task ShouldRunBackgroundCompactionAtL0Trigger()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithPerformanceGoal(PantsPerformanceGoal.Latency)
                .WithBackgroundCompaction(true));
        for (int index = 0; index < 3; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        PantsStorageLayout layout = await database.GetStorageLayoutAsync();
        PantsStorageLevelLayout level = Assert.Single(layout.Levels);
        Assert.Equal(1, level.Level);
        Assert.Equal(1, level.FileCount);
        Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).CompactionsRun);
    }

    [Fact]
    public async Task ShouldKeepCompactionInputSetBoundedAndReportMultipleLevels()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        for (int index = 0; index < 65; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        await database.CompactAllAsync();

        PantsStorageLayout layout = await database.GetStorageLayoutAsync();
        Assert.Equal([0, 1], layout.Levels.Select(static level => level.Level));
        Assert.Equal(2, layout.Levels.Single(static level => level.Level == 0).FileCount);
        Assert.Equal(21, layout.Levels.Single(static level => level.Level == 1).FileCount);
    }

    [Fact]
    public async Task ShouldChangeBackgroundCompactionAtRuntime()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3)));
        for (int index = 0; index < 3; index++)
        {
            await PutAndFlushAsync(database, index);
        }

        Assert.Equal(3, Assert.Single((await database.GetStorageLayoutAsync()).Levels).FileCount);

        await database.SetBackgroundCompactionAsync(true);
        await PutAndFlushAsync(database, 3);

        PantsStorageLayout layout = await database.GetStorageLayoutAsync();
        Assert.Contains(layout.Levels, static level => level.Level == 1);
    }

    private static async ValueTask PutAndFlushAsync(IPantsDatabase database, int index)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(
            TestBytes.FromString($"key-{index:D4}"),
            TestBytes.FromString($"value-{index:D4}"));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.FlushAsync(database.DefaultColumnFamily);
    }
}
