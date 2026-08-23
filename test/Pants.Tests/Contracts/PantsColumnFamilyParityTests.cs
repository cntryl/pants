namespace Cntryl.Pants.Tests;

public sealed class PantsColumnFamilyParityTests
{
    [Fact]
    public async Task ShouldValidateNamesAndAllocateMonotonicColumnFamilyIdentities()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        string maximumName = $"{new string('\u00e9', 127)}a";

        IPantsColumnFamily first = await database.CreateColumnFamilyAsync("first");
        IPantsColumnFamily duplicate = await database.CreateColumnFamilyAsync("first");
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Name, duplicate.Name);
        Assert.Equal(2, (await database.ListColumnFamiliesAsync()).Count);
        await database.DropColumnFamilyAsync(first);
        IPantsColumnFamily recreated = await database.CreateColumnFamilyAsync("first");
        IPantsColumnFamily maximum = await database.CreateColumnFamilyAsync(maximumName);

        Assert.True(recreated.Id > first.Id);
        Assert.Equal(maximumName, maximum.Name);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.CreateColumnFamilyAsync(string.Empty).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.CreateColumnFamilyAsync("default").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.CreateColumnFamilyAsync("contains\0nul").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.CreateColumnFamilyAsync(new string('\u00e9', 128)).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.BeginTransactionAsync(first, PantsTransactionMode.ReadOnly).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.DropColumnFamilyAsync(first).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.DropColumnFamilyAsync(database.DefaultColumnFamily).AsTask());
    }

    [Fact]
    public async Task ShouldIsolateAndPersistColumnFamiliesAcrossFlushAndCompaction()
    {
        using var directory = new TemporaryDirectory();
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            IPantsColumnFamily alpha = await database.CreateColumnFamilyAsync("alpha");
            IPantsColumnFamily beta = await database.CreateColumnFamilyAsync("beta");
            await PutAsync(database, database.DefaultColumnFamily, "shared", "default");
            await PutAsync(database, alpha, "shared", "alpha");
            await PutAsync(database, beta, "shared", "beta");
            await database.FlushAsync(database.DefaultColumnFamily);
            await database.FlushAsync(alpha);
            await database.FlushAsync(beta);
            await database.CompactAllAsync();
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        IPantsColumnFamily alphaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("alpha"));
        IPantsColumnFamily betaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("beta"));

        Assert.Equal("default", await ReadAsync(reopened, reopened.DefaultColumnFamily, "shared"));
        Assert.Equal("alpha", await ReadAsync(reopened, alphaReopened, "shared"));
        Assert.Equal("beta", await ReadAsync(reopened, betaReopened, "shared"));
        Assert.Equal(
            ["default", "alpha", "beta"],
            (await reopened.ListColumnFamiliesAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldDistinguishSafeAndDestructiveColumnFamilyDrop()
    {
        using var directory = new TemporaryDirectory();
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            IPantsColumnFamily safe = await database.CreateColumnFamilyAsync("safe");
            IPantsColumnFamily destructive = await database.CreateColumnFamilyAsync("destructive");
            await PutAsync(database, safe, "key", "safe");
            await PutAsync(database, destructive, "key", "discarded");

            PantsBusyException busy = await Assert.ThrowsAsync<PantsBusyException>(
                () => database.DropColumnFamilyAsync(safe).AsTask());
            Assert.Equal(PantsErrorCode.Busy, busy.Code);

            await database.FlushAsync(safe);
            await database.DropColumnFamilyAsync(safe);
            await database.DropColumnFamilyDiscardingUnflushedAsync(destructive);
            Assert.Null(await database.GetColumnFamilyAsync("safe"));
            Assert.Null(await database.GetColumnFamilyAsync("destructive"));
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.GetColumnFamilyAsync("safe"));
        Assert.Null(await reopened.GetColumnFamilyAsync("destructive"));
        Assert.Equal(["default"], (await reopened.ListColumnFamiliesAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldRejectCommitThroughHandleDroppedAfterTransactionStart()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        IPantsColumnFamily family = await database.CreateColumnFamilyAsync("temporary");
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await database.DropColumnFamilyAsync(family);

        PantsInvalidArgumentException error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldIsolateIndependentInMemoryDatabaseInstances()
    {
        await using IPantsDatabase first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsDatabase second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(first, first.DefaultColumnFamily, "key", "first");
        await PutAsync(second, second.DefaultColumnFamily, "key", "second");

        Assert.Equal("first", await ReadAsync(first, first.DefaultColumnFamily, "key"));
        Assert.Equal("second", await ReadAsync(second, second.DefaultColumnFamily, "key"));
    }

    [Fact]
    public async Task ShouldKeepInMemoryOperationsEphemeralAndIsolated()
    {
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()))
        {
            IPantsColumnFamily family = await database.CreateColumnFamilyAsync("ephemeral");
            await using (IPantsTransaction writes = await database.BeginTransactionAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 100; index++)
                {
                    writes.Put(TestBytes.FromString($"key-{index:000}"), "initial"u8.ToArray());
                }

                await writes.CommitAsync(PantsWriteOptions.Buffered);
            }

            await using (IPantsTransaction mutations = await database.BeginTransactionAsync(
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

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        Assert.Null(await reopened.GetColumnFamilyAsync("ephemeral"));
    }

    private static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    private static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadOnly);
        ReadOnlyMemory<byte>? value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
