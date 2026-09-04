namespace Cntryl.Pants.Runtime;

public sealed class PantsDatabaseLifecycleTests
{
    [Fact]
    public async Task ShouldRejectInvalidTransactionMode()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        var error = await Assert.ThrowsAnyAsync<PantsException>(() => database.Transactions
            .BeginAsync(database.ColumnFamilies.DefaultFamily, (PantsTransactionMode)int.MaxValue)
            .AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldRejectInvalidConflictPolicy()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);

        var error = Assert.ThrowsAny<PantsException>(() =>
            transaction.SetConflictPolicy((PantsConflictPolicy)int.MaxValue));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldRemainClosingWhenShutdownIsBlockedByTransaction()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var error = await Assert.ThrowsAsync<PantsBusyException>(() => database
            .ShutdownAsync(TimeSpan.FromSeconds(1))
            .AsTask());

        Assert.Equal(PantsErrorCode.Busy, error.Code);
        await Assert.ThrowsAsync<PantsBusyException>(() => transaction.GetAsync("missing"u8.ToArray()).AsTask());
        var independentBegin = Task.Run(async () => await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly));
        var independentCreate = Task.Run(async () => await database.ColumnFamilies.CreateAsync(
            "closing-family"));

        await Assert.ThrowsAsync<PantsBusyException>(() => independentBegin);
        await Assert.ThrowsAsync<PantsBusyException>(() => independentCreate);

        await transaction.RollbackAsync();
        await database.ShutdownAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<PantsAbortedException>(() => database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly)
            .AsTask());
    }
}
