using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsCompactionPublicationMetricsTests
{
    [Fact]
    public async Task ShouldRecordEachPhysicalPublicationAndItsOutputBytesGivenRecursiveCompaction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        await SeedTwoL0FilesAsync(database);

        await database.Maintenance.CompactAllAsync();

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        var level = Assert.Single(layout.Levels);
        Assert.Equal(2, level.Level);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(2, metrics.CompactionsRun);
        Assert.Equal(checked(level.TotalBytes * 2), metrics.CompactionBytesRewritten);
        Assert.Equal(0, metrics.CompactionFailures);
    }

    [Fact]
    public async Task ShouldPreserveCompletedPublicationMetricsGivenSecondPublicationFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new NthCompactionPublicationFailpointHandler(2);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoint));
        await SeedTwoL0FilesAsync(database);

        await Assert.ThrowsAsync<PantsIOException>(() => database.Maintenance.CompactAllAsync().AsTask());

        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        var level = Assert.Single(layout.Levels);
        Assert.Equal(1, level.Level);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.CompactionsRun);
        Assert.Equal(level.TotalBytes, metrics.CompactionBytesRewritten);
        Assert.Equal(1, metrics.CompactionFailures);
        Assert.Equal(2, failpoint.HitCount);
        Assert.Equal(1, failpoint.FailureCount);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1,
                MaximumLevels: 3,
                BackgroundEnabled: false));

    static async Task SeedTwoL0FilesAsync(IPantsDatabase database)
    {
        for (var index = 0; index < 2; index++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"key-{index}"),
                TestBytes.FromString($"value-{index}"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
    }
}
