namespace Cntryl.Pants.Tests.Runtime;

public sealed class ReadOnlyCoordinatorFastPathTests
{
    [Fact]
    public async Task ShouldEnqueueNoCoordinatorCommandsGivenOrdinaryReadOnlyPointTransaction()
    {
        await using var database = Assert.IsType<DatabaseInstance>(
            await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()));
        var before = database.CoordinatorCommandsEnqueued;

        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
        }

        Assert.Equal(before, database.CoordinatorCommandsEnqueued);
    }
}
