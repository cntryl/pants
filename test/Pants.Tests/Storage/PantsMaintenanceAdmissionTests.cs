using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsMaintenanceAdmissionTests
{
    /// <summary>
    ///     The interaction that makes maintenance admission dangerous: writes stall on level-0 debt,
    ///     and flush and compaction are the only things that reduce it. If a tight local budget can
    ///     refuse them, the database never accepts another write. Draining must stay possible with
    ///     the budget saturated.
    /// </summary>
    [Fact]
    public async Task ShouldStillDrainL0WhenTheLocalStorageBudgetIsSaturated()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "admission/")
                .WithSimulatedCloudLocalStorageBudget(96 * 1024)
                .WithBackgroundCompaction(false)
                .WithCompaction(new PantsCompactionConfiguration(
                    L0FileCountTrigger: 2,
                    BackgroundEnabled: false)));
        var family = database.ColumnFamilies.DefaultFamily;

        for (var index = 0; index < 12; index++)
        {
            try
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"key-{index}"),
                    new byte[8 * 1024]);
                await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
                await database.Maintenance.FlushAsync(family);
            }
            catch (PantsWriteStallException)
            {
                break;
            }
        }

        // Without this the test could pass by never accruing debt at all, exercising none of the
        // path it exists to cover.
        var before = await CountL0FilesAsync(database);
        Assert.True(before > 0, "Expected level-0 debt to have accrued.");

        await database.Maintenance.CompactAllAsync();

        Assert.True(
            await CountL0FilesAsync(database) < before,
            "Compaction must make progress even with the local budget under pressure.");

        await using var writer = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        writer.Put(TestBytes.FromString("after-drain"), TestBytes.FromString("value"));
        await writer.CommitAsync(PantsWriteOptions.CloudAsync);
    }

    static async Task<int> CountL0FilesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync())
        .Levels.FirstOrDefault(level => level.Level == 0)?.FileCount ?? 0;
}
