using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsColumnFamilyParityTests
{
    [Fact]
    public async Task ShouldValidateNamesAndAllocateMonotonicColumnFamilyIdentities()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var maximumName = $"{new string('\u00e9', 127)}a";

        var first = await database.ColumnFamilies.CreateAsync("first");
        var duplicate = await database.ColumnFamilies.CreateAsync("first");
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Name, duplicate.Name);
        Assert.Equal(2, (await database.ColumnFamilies.ListAsync()).Count);
        await database.ColumnFamilies.DropAsync(first);
        var recreated = await database.ColumnFamilies.CreateAsync("first");
        var maximum = await database.ColumnFamilies.CreateAsync(maximumName);

        Assert.True(recreated.Id > first.Id);
        Assert.Equal(maximumName, maximum.Name);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync(string.Empty).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync("default").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync("contains\0nul").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync(new string('\u00e9', 128)).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.Transactions.BeginAsync(first, PantsTransactionMode.ReadOnly).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.DropAsync(first).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.DropAsync(database.ColumnFamilies.DefaultFamily).AsTask());
    }

    [Fact]
    public async Task ShouldIsolateAndPersistColumnFamiliesAcrossFlushAndCompaction()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            var alpha = await database.ColumnFamilies.CreateAsync("alpha");
            var beta = await database.ColumnFamilies.CreateAsync("beta");
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "shared", "default");
            await PutAsync(database, alpha, "shared", "alpha");
            await PutAsync(database, beta, "shared", "beta");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.FlushAsync(alpha);
            await database.Maintenance.FlushAsync(beta);
            await database.Maintenance.CompactAllAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var alphaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("alpha"));
        var betaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("beta"));

        Assert.Equal("default", await ReadAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "shared"));
        Assert.Equal("alpha", await ReadAsync(reopened, alphaReopened, "shared"));
        Assert.Equal("beta", await ReadAsync(reopened, betaReopened, "shared"));
        Assert.Equal(
            ["default", "alpha", "beta"],
            (await reopened.ColumnFamilies.ListAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldDistinguishSafeAndDestructiveColumnFamilyDrop()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            var safe = await database.ColumnFamilies.CreateAsync("safe");
            var destructive = await database.ColumnFamilies.CreateAsync("destructive");
            await PutAsync(database, safe, "key", "safe");
            await PutAsync(database, destructive, "key", "discarded");

            var busy = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ColumnFamilies.DropAsync(safe).AsTask());
            Assert.Equal(PantsErrorCode.Busy, busy.Code);

            await database.Maintenance.FlushAsync(safe);
            await database.ColumnFamilies.DropAsync(safe);
            await database.ColumnFamilies.DropDiscardingUnflushedAsync(destructive);
            Assert.Null(await database.ColumnFamilies.GetAsync("safe"));
            Assert.Null(await database.ColumnFamilies.GetAsync("destructive"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("safe"));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("destructive"));
        Assert.Equal(["default"], (await reopened.ColumnFamilies.ListAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldRejectCommitThroughHandleDroppedAfterTransactionStart()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("temporary");
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await database.ColumnFamilies.DropAsync(family);

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldIsolateIndependentInMemoryDatabaseInstances()
    {
        await using var first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(first, first.ColumnFamilies.DefaultFamily, "key", "first");
        await PutAsync(second, second.ColumnFamilies.DefaultFamily, "key", "second");

        Assert.Equal("first", await ReadAsync(first, first.ColumnFamilies.DefaultFamily, "key"));
        Assert.Equal("second", await ReadAsync(second, second.ColumnFamilies.DefaultFamily, "key"));
    }

    [Fact]
    public async Task ShouldKeepInMemoryOperationsEphemeralAndIsolated()
    {
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()))
        {
            var family = await database.ColumnFamilies.CreateAsync("ephemeral");
            await using (var writes = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 100; index++)
                {
                    writes.Put(TestBytes.FromString($"key-{index:000}"), "initial"u8.ToArray());
                }

                await writes.CommitAsync(PantsWriteOptions.Buffered);
            }

            await using (var mutations = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 50; index++)
                {
                    mutations.Delete(TestBytes.FromString($"key-{index:000}"));
                }

                mutations.Put("key-050"u8.ToArray(), "updated"u8.ToArray());
                await mutations.CommitAsync(PantsWriteOptions.Buffered);
            }

            Assert.Null(await ReadAsync(database, family, "key-000"));
            Assert.Equal("updated", await ReadAsync(database, family, "key-050"));
            Assert.Equal("initial", await ReadAsync(database, family, "key-099"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        Assert.Null(await reopened.ColumnFamilies.GetAsync("ephemeral"));
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
