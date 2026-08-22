namespace Pants.Tests;

public sealed class PantsWalSyncCountContractTests
{
    [Fact]
    public async Task ShouldIssueOnePhysicalWalSyncGivenNonEmptySyncCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var before = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        var after = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        Assert.Equal(1, after - before);
    }

    [Fact]
    public async Task ShouldIssueOnePhysicalWalSyncGivenSpilledSyncCommit()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1024))
            .WithMemtableLimits(1024)
            .WithTransactionMemoryPool(1024);
        await using var database = await PantsDatabase.OpenAsync(options);
        var before = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        var value = new byte[900];
        for (var index = 0; index < 6; index++)
        {
            transaction.Put(TestBytes.FromString($"key-{index}"), value);
        }

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        await transaction.CommitAsync(PantsWriteOptions.Sync);

        var after = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        Assert.Equal(1, after - before);
    }

    [Fact]
    public async Task ShouldIssueOnePhysicalWalSyncGivenEmptySyncCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var before = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        var after = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        Assert.Equal(1, after - before);
    }

    [Fact]
    public async Task ShouldIssueOnePhysicalWalSyncGivenAssertionOnlySyncCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using (var seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("guard"u8.ToArray(), "value"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        var before = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.AssertValue("guard"u8.ToArray(), "value"u8.ToArray());

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        var after = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        Assert.Equal(1, after - before);
    }

    [Fact]
    public async Task ShouldNotIssuePhysicalWalSyncGivenBufferedCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var before = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await transaction.CommitAsync(PantsWriteOptions.Buffered);

        var after = (await database.GetRuntimeMetricsAsync()).WalFsyncCount;
        Assert.Equal(0, after - before);
    }
}
