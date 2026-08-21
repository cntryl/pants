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
        Assert.Equal(1, layout.Levels.Single(static level => level.Level == 0).FileCount);
        Assert.Equal(1, layout.Levels.Single(static level => level.Level == 1).FileCount);
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
