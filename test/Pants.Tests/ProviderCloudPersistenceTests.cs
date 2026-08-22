namespace Pants.Tests;

public sealed class ProviderCloudPersistenceTests
{
    [Fact]
    public async Task ShouldFailCloudStrictCommitGivenWalUploadFailure()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, location),
            dependencies);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        handler.FailWalWrites = true;

        await Assert.ThrowsAsync<PantsIOException>(
            () => transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

        handler.FailWalWrites = false;
    }

    [Fact]
    public async Task ShouldKeepCloudAsyncCommitVisibleGivenWalUploadFailure()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation()),
            dependencies);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            handler.FailWalWrites = true;
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("key"u8.ToArray()))));

        handler.FailWalWrites = false;
    }

    [Fact]
    public async Task ShouldRecoverCloudStrictCommitGivenEmptyReplacementCache()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);

        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(firstCache.Path, location),
                         dependencies))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(secondCache.Path, location),
            dependencies);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("key"u8.ToArray()))));
    }

    static PantsCloudStorageLocation CreateAzureLocation() =>
        new(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");
}
