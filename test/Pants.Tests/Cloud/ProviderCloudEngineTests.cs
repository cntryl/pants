namespace Cntryl.Pants.Tests;

public sealed class ProviderCloudEngineTests
{
    [Fact]
    public async Task ShouldApplyLastWriteWinsGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await PutAsync(context.Database, "key", "first");
        await PutAsync(context.Database, "key", "second");

        Assert.Equal("second", await GetAsync(context.Database, "key"));
    }

    [Fact]
    public async Task ShouldCreateColumnFamilyGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();

        var columnFamily = await context.Database.CreateColumnFamilyAsync("orders");
        await PutAsync(context.Database, columnFamily, "key", "value");

        Assert.Equal("value", await GetAsync(context.Database, columnFamily, "key"));
    }

    [Fact]
    public async Task ShouldHideDeletedKeyGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await PutAsync(context.Database, "key", "value");
        await using (var transaction = await context.Database.BeginTransactionAsync(
                         context.Database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Delete("key"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        Assert.Null(await GetAsync(context.Database, "key"));
    }

    [Fact]
    public async Task ShouldPreserveSnapshotValueGivenProviderCloudOverwrite()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await PutAsync(context.Database, "key", "first");
        await using var snapshot = await context.Database.BeginTransactionAsync(
            context.Database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        await PutAsync(context.Database, "key", "second");

        Assert.Equal("first", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await snapshot.GetAsync("key"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldReadCommittedValueGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();

        await PutAsync(context.Database, "key", "value");

        Assert.Equal("value", await GetAsync(context.Database, "key"));
    }

    [Fact]
    public async Task ShouldReadOwnWriteGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await using var transaction = await context.Database.BeginTransactionAsync(
            context.Database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await transaction.GetAsync("key"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldScanInsertedKeysGivenProviderCloudMode()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await PutAsync(context.Database, "a", "one");
        await PutAsync(context.Database, "b", "two");
        await using var transaction = await context.Database.BeginTransactionAsync(
            context.Database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var keys = new List<string>();
        await foreach (var entry in scan)
        {
            keys.Add(TestBytes.ToText(entry.Key));
        }

        Assert.Equal(["a", "b"], keys);
    }

    [Fact]
    public async Task ShouldRejectSyncAndBufferedDurabilityGivenProviderCloudWrite()
    {
        await using var context = await ProviderCloudTestContext.CreateAsync();
        await using var sync = await context.Database.BeginTransactionAsync(
            context.Database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        sync.Put("sync"u8.ToArray(), "value"u8.ToArray());
        await Assert.ThrowsAsync<PantsNotSupportedException>(
            () => sync.CommitAsync(PantsWriteOptions.Sync).AsTask());
        await using var buffered = await context.Database.BeginTransactionAsync(
            context.Database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        buffered.Put("buffered"u8.ToArray(), "value"u8.ToArray());

        await Assert.ThrowsAsync<PantsNotSupportedException>(
            () => buffered.CommitAsync(PantsWriteOptions.Buffered).AsTask());
    }

    static ValueTask PutAsync(IPantsDatabase database, string key, string value) =>
        PutAsync(database, database.DefaultColumnFamily, key, value);

    static async ValueTask PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static ValueTask<string?> GetAsync(IPantsDatabase database, string key) =>
        GetAsync(database, database.DefaultColumnFamily, key);

    static async ValueTask<string?> GetAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value.HasValue ? TestBytes.ToText(value.Value) : null;
    }
}
