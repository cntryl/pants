namespace Pants.Tests;

public sealed class PantsDatabaseLifecycleTests
{
    [Fact]
    public async Task ShouldRejectInvalidTransactionMode()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() => database
            .BeginTransactionAsync(database.DefaultColumnFamily, (PantsTransactionMode)int.MaxValue)
            .AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldRejectInvalidConflictPolicy()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        PantsException error = Assert.ThrowsAny<PantsException>(() =>
            transaction.SetConflictPolicy((PantsConflictPolicy)int.MaxValue));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldKeepDatabaseOpenWhenShutdownIsBlockedByTransaction()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() => database
            .ShutdownAsync(TimeSpan.FromSeconds(1))
            .AsTask());

        Assert.Equal(PantsErrorCode.Busy, error.Code);
        Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
    }
}
