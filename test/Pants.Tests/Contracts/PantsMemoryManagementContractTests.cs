namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsMemoryManagementContractTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldMakeProgressWithoutStickyStallGivenSmallMemoryBudget(string mode)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(mode, directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024));
        await using var database = await PantsDatabase.OpenAsync(options);
        var committed = 0;
        var stalled = false;

        for (var index = 0; index < 64; index++)
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString($"key-{index:000}"), new byte[1024]);
            try
            {
                await transaction.CommitAsync(GetWriteOptions(mode));
                committed++;
            }
            catch (PantsWriteStallException)
            {
                stalled = true;
                break;
            }

            if (index % 10 == 0)
            {
                await database.FlushAsync(database.DefaultColumnFamily);
            }
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.True(stalled || committed > 0);
        Assert.NotEqual(PantsEngineHealth.WriteStalled, metrics.Health);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldCompleteDisposalGivenWalWriterHasPendingData(string mode)
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path));
        await StorageModeTestHarness.PutAsync(database, mode, "key", "value");

        await database.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    static PantsOpenOptions CreateOptions(string mode, string path) =>
        mode switch
        {
            "memory" => PantsOpenOptions.InMemory(),
            "local" => PantsOpenOptions.Local(path),
            "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "memory-management/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        };

    static PantsWriteOptions GetWriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;
}
