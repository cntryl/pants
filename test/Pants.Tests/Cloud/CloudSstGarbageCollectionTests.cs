using System.Text;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudSstGarbageCollectionTests
{
    [Fact]
    public async Task ShouldRetainReusableSstNameGivenRemoteManifestFrontier()
    {
        var store = new TestCloudObjectStore();
        const string name = "000000_01_00000000000000000007.sst";
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        Assert.True(await store.PutAsync(
            objectKey,
            "orphan"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        var collector = new CloudSstGarbageCollector(
            store,
            _ => ValueTask.FromResult(new CloudSstRetentionProof(
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<uint, ulong> { [0] = 7 },
                [])),
            () => new HashSet<string>(StringComparer.Ordinal),
            static () => { },
            NullPantsFailpointHandler.Instance);

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.NotNull(await store.HeadAsync(objectKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldAdoptRemoteCompactionOrphanGivenSuccessorCacheRecovery()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var finalCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var failpoints = new CloudCompactionFailpointHandler();
        var initialNames = new HashSet<string>(StringComparer.Ordinal);
        string orphanPath;
        await using (var database = await OpenProviderAsync(
                         firstCache.Path,
                         client,
                         failpoints))
        {
            await PrepareTwoSstsAsync(database, "successor-adoption");
            initialNames.UnionWith(handler.GetObjectPaths("/sst/"));
            failpoints.Arm(PantsFailpoint.BeforeCompactionManifestPublish);

            await Assert.ThrowsAsync<PantsIOException>(() => database.CompactAllAsync().AsTask());

            orphanPath = Assert.Single(
                handler.GetObjectPaths("/sst/"),
                path => !initialNames.Contains(path));
        }

        await using (var successor = await OpenProviderAsync(
                         secondCache.Path,
                         client))
        {
            Assert.Contains(
                orphanPath,
                handler.GetObjectPaths("/sst/"),
                StringComparer.Ordinal);

            await successor.CompactAllAsync();

            Assert.Equal(orphanPath, Assert.Single(handler.GetObjectPaths("/sst/")));
            await AssertReadableAsync(successor, "successor-adoption");
        }

        await using var reopened = await OpenProviderAsync(finalCache.Path, client);
        await AssertReadableAsync(reopened, "successor-adoption");
    }

    [Fact]
    public async Task ShouldCollectOrphanedCloudObjectsAfterCompaction()
    {
        await VerifyProviderCollectionAsync();
        await VerifySimulatedCollectionAsync();
    }

    [Fact]
    public async Task ShouldNotCollectCloudObjectsReferencedByManifest()
    {
        await VerifyProviderReferenceRetentionAsync();
        await VerifySimulatedReferenceRetentionAsync();
    }

    [Fact]
    public async Task ShouldHandleGcWhenCloudListFails()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "list-failure");
        var obsoletePaths = handler.GetObjectPaths("/sst/");
        Assert.Equal(2, obsoletePaths.Length);
        handler.FailSstList = true;

        await database.CompactAllAsync();

        Assert.All(obsoletePaths, path => Assert.Contains(
            path,
            handler.GetObjectPaths("/sst/"),
            StringComparer.Ordinal));
        Assert.Equal(3, handler.GetObjectPaths("/sst/").Length);
        Assert.Equal(0, handler.SstDeleteAttempts);
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.GetRuntimeMetricsAsync()).Health);
        await AssertReadableAsync(database, "list-failure");
        handler.FailSstList = false;
    }

    [Fact]
    public async Task ShouldHandleGcWhenCloudDeleteFails()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "delete-failure");
        var obsoletePaths = handler.GetObjectPaths("/sst/");
        Assert.Equal(2, obsoletePaths.Length);
        handler.FailSstDeletes = true;

        await database.CompactAllAsync();

        Assert.All(obsoletePaths, path => Assert.Contains(
            path,
            handler.GetObjectPaths("/sst/"),
            StringComparer.Ordinal));
        Assert.Equal(3, handler.GetObjectPaths("/sst/").Length);
        Assert.True(handler.SstDeleteAttempts > 0);
        Assert.Equal(0, handler.UnconditionalSstDeleteAttempts);
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.GetRuntimeMetricsAsync()).Health);
        await AssertReadableAsync(database, "delete-failure");
        handler.FailSstDeletes = false;
    }

    static async Task VerifyProviderCollectionAsync()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "provider-collection");

        await database.CompactAllAsync();

        Assert.Single(handler.GetObjectPaths("/sst/"));
        Assert.Equal(2, handler.SstDeleteAttempts);
        Assert.Equal(0, handler.UnconditionalSstDeleteAttempts);
        await AssertReadableAsync(database, "provider-collection");
    }

    static async Task VerifySimulatedCollectionAsync()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "database/")
                .WithBackgroundCompaction(false));
        await PrepareTwoSstsAsync(database, "simulated-collection");

        await database.CompactAllAsync();

        Assert.Single(GetSimulatedCloudSstPaths(directory.Path));
        await AssertReadableAsync(database, "simulated-collection");
    }

    static async Task VerifyProviderReferenceRetentionAsync()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await CommitAndFlushAsync(database, "provider-reference/key");
        var referencedPath = Assert.Single(handler.GetObjectPaths("/sst/"));

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal(referencedPath, Assert.Single(handler.GetObjectPaths("/sst/")));
        await AssertValueAsync(database, "provider-reference/key");
    }

    static async Task VerifySimulatedReferenceRetentionAsync()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "database/")
                .WithBackgroundCompaction(false));
        await CommitAndFlushAsync(database, "simulated-reference/key");
        var referencedPath = Assert.Single(GetSimulatedCloudSstPaths(directory.Path));

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal(referencedPath, Assert.Single(GetSimulatedCloudSstPaths(directory.Path)));
        await AssertValueAsync(database, "simulated-reference/key");
    }

    static ValueTask<IPantsDatabase> OpenProviderAsync(string path, HttpClient client) =>
        OpenProviderAsync(path, client, NullPantsFailpointHandler.Instance);

    static ValueTask<IPantsDatabase> OpenProviderAsync(
        string path,
        HttpClient client,
        IPantsFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(path, CreateAzureLocation())
                .WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(
                failpoints,
                cloudHttpClient: client));

    static PantsCloudStorageLocation CreateAzureLocation() =>
        new(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");

    static async Task PrepareTwoSstsAsync(IPantsDatabase database, string prefix)
    {
        await CommitAndFlushAsync(database, $"{prefix}/first");
        await CommitAndFlushAsync(database, $"{prefix}/second");
        Assert.Equal(2, (await database.GetRuntimeMetricsAsync()).SstCount);
    }

    static async Task CommitAndFlushAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(Encoding.UTF8.GetBytes(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.FlushAsync(database.DefaultColumnFamily);
    }

    static async Task AssertReadableAsync(IPantsDatabase database, string prefix)
    {
        await AssertValueAsync(database, $"{prefix}/first");
        await AssertValueAsync(database, $"{prefix}/second");
    }

    static async Task AssertValueAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(Encoding.UTF8.GetBytes(key));
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
    }

    static string[] GetSimulatedCloudSstPaths(string root) =>
        Directory.GetFiles(
            Path.Combine(root, "cloud_store", "sst"),
            "*.sst",
            SearchOption.TopDirectoryOnly);
}
