using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsTinyMemoryBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ShouldRejectPersistentBudgetThatLeavesNoCompactionPool(long budgetBytes)
    {
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path)
                    .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes))));
    }

    [Fact]
    public async Task ShouldStillOpenInMemoryDatabaseWithATinyBudget()
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024)));

        Assert.NotNull(database);
    }
}
