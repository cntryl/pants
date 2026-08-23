namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsDatabaseLifecycleTests
{
    [Fact]
    public async Task ShouldRejectInvalidTransactionMode()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        var error = await Assert.ThrowsAnyAsync<PantsException>(() => database
            .BeginTransactionAsync(database.DefaultColumnFamily, (PantsTransactionMode)int.MaxValue)
            .AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldRejectInvalidConflictPolicy()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        var error = Assert.ThrowsAny<PantsException>(() =>
            transaction.SetConflictPolicy((PantsConflictPolicy)int.MaxValue));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldRemainClosingWhenShutdownIsBlockedByTransaction()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var error = await Assert.ThrowsAsync<PantsBusyException>(() => database
            .ShutdownAsync(TimeSpan.FromSeconds(1))
            .AsTask());

        Assert.Equal(PantsErrorCode.Busy, error.Code);
        await Assert.ThrowsAsync<PantsBusyException>(() => transaction.GetAsync("missing"u8.ToArray()).AsTask());

        await transaction.RollbackAsync();
        await database.ShutdownAsync(TimeSpan.FromSeconds(1));
    }
}
