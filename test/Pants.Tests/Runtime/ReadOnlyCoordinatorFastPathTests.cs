using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class ReadOnlyCoordinatorFastPathTests
{
    [Fact]
    public async Task ShouldEnqueueNoCoordinatorCommandsGivenOrdinaryReadOnlyPointTransaction()
    {
        await using var database = Assert.IsType<DatabaseInstance>(
            await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()));
        var before = database.CoordinatorCommandsEnqueued;

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
        }

        Assert.Equal(before, database.CoordinatorCommandsEnqueued);
    }

    [Fact]
    public async Task ShouldEnqueueNoCoordinatorCommandsGivenSstResidentReadOnlyPointTransaction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = Assert.IsType<DatabaseInstance>(
            await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)));
        var family = database.ColumnFamilies.DefaultFamily;
        await using (var writer = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("sst-key"u8.ToArray(), "sst-value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await database.Maintenance.FlushAsync(family);
        var before = database.CoordinatorCommandsEnqueued;

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(
                "sst-value"u8.ToArray(),
                (await transaction.GetAsync("sst-key"u8.ToArray()))?.ToArray());
        }

        Assert.Equal(before, database.CoordinatorCommandsEnqueued);
    }
}
