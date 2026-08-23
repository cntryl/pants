namespace Cntryl.Pants.Tests.Support.TestDoubles;

sealed class ProviderCloudTestContext : IAsyncDisposable
{
    readonly TemporaryDirectory _cache;
    readonly HttpClient _client;

    ProviderCloudTestContext(
        TemporaryDirectory cache,
        InMemoryAzureBlobHandler handler,
        HttpClient client,
        IPantsDatabase database)
    {
        _cache = cache;
        Handler = handler;
        _client = client;
        Database = database;
    }

    public InMemoryAzureBlobHandler Handler { get; }

    public IPantsDatabase Database { get; }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
        _cache.Dispose();
    }

    public static async ValueTask<ProviderCloudTestContext> CreateAsync()
    {
        var cache = new TemporaryDirectory();
        var handler = new InMemoryAzureBlobHandler();
        var client = new HttpClient(handler);
        try
        {
            var location = new PantsCloudStorageLocation(
                new PantsCloudProviderConfiguration.AzureBlob(
                    "account",
                    "container",
                    new Uri("https://storage.example.test"),
                    new PantsAzureCredentialSource.SasToken("sig=test")),
                $"database-{Guid.NewGuid():N}");
            var database = await PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Cloud(cache.Path, location),
                new RuntimeDependencies(cloudHttpClient: client));
            return new ProviderCloudTestContext(cache, handler, client, database);
        }
        catch
        {
            client.Dispose();
            cache.Dispose();
            throw;
        }
    }
}
