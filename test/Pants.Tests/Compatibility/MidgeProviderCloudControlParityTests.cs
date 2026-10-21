using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeProviderCloudControlParityTests
{
    [Fact]
    public async Task ShouldPreserveAcknowledgedWritesAcrossFlushCompactionAndFreshCacheTakeovers()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var thirdCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);

        await using (var first = await OpenAsync(firstCache.Path, location, dependencies))
        {
            await PutAsync(first, "strict-before"u8.ToArray(), "one"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await PutAsync(first, "async-before"u8.ToArray(), "two"u8.ToArray(),
                PantsWriteOptions.CloudAsync);
            await PutAsync(first, "deleted"u8.ToArray(), "obsolete"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await DeleteAsync(first, "deleted"u8.ToArray(), PantsWriteOptions.CloudStrict);
            await first.Maintenance.FlushAsync(first.ColumnFamilies.DefaultFamily);
            await first.Maintenance.CompactAllAsync();
            await first.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var takeover = await OpenAsync(secondCache.Path, location, dependencies))
        {
            await PutAsync(takeover, "strict-after"u8.ToArray(), "three"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await PutAsync(takeover, "async-after"u8.ToArray(), "four"u8.ToArray(),
                PantsWriteOptions.CloudAsync);
            await takeover.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var recovered = await OpenAsync(thirdCache.Path, location, dependencies);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        await AssertValueAsync(reader, "strict-before"u8.ToArray(), "one"u8.ToArray());
        await AssertValueAsync(reader, "async-before"u8.ToArray(), "two"u8.ToArray());
        await AssertValueAsync(reader, "strict-after"u8.ToArray(), "three"u8.ToArray());
        await AssertValueAsync(reader, "async-after"u8.ToArray(), "four"u8.ToArray());
        Assert.Null(await reader.GetAsync("deleted"u8.ToArray()));
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string cachePath,
        PantsCloudStorageLocation location,
        RuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cachePath, location).WithBackgroundCompaction(false),
            dependencies);

    static async Task PutAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        PantsWriteOptions options)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
        await transaction.CommitAsync(options);
    }

    static async Task DeleteAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key,
        PantsWriteOptions options)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Delete(key);
        await transaction.CommitAsync(options);
    }

    static async Task AssertValueAsync(
        IPantsTransaction reader,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> expected) =>
        Assert.Equal(
            TestBytes.ToText(expected),
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync(key))));
}
