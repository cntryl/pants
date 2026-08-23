namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsProviderCloudStartupCleanupTests
{
    [Fact]
    public async Task ShouldReleaseCloudLeaseAfterStrictHydrationFailure()
    {
        using var initialCache = new TemporaryDirectory();
        using var recoveryCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        await using (var database = await OpenAsync(
                         initialCache.Path,
                         location,
                         dependencies))
        {
            await CommitCloudStrictAsync(database);
        }

        var remoteWal = Assert.Single(handler.GetObjectPaths("/wal/epochs/"));
        handler.ReplaceObjectText(remoteWal, "corrupt");
        var recoveryOptions = PantsOpenOptions.Cloud(recoveryCache.Path, location)
            .WithRecoveryPolicy(PantsRecoveryPolicy.Strict)
            .WithBackgroundCompaction(false);

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => PantsDatabase.OpenForTestingAsync(
            recoveryOptions,
            dependencies).AsTask());

        await using var salvaged = await PantsDatabase.OpenForTestingAsync(
            recoveryOptions.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage),
            dependencies);
        Assert.Equal(
            PantsEngineHealth.SalvageMode,
            (await salvaged.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldReleaseCloudAndLocalLeasesAfterWalCatalogFencingFailure()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler
        {
            AcknowledgeWalCatalogWritesWithoutPersisting = true
        };
        using var client = new HttpClient(handler);
        var location = CreateLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, location)
            .WithBackgroundCompaction(false);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask());

        handler.AcknowledgeWalCatalogWritesWithoutPersisting = false;
        await using var corrected = await PantsDatabase.OpenForTestingAsync(
            options,
            dependencies);
        Assert.True(corrected.IsPrimaryLeaseHealthy);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string cachePath,
        PantsCloudStorageLocation location,
        PantsRuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cachePath, location)
                .WithBackgroundCompaction(false),
            dependencies);

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsCloudProviderConfiguration.AzureBlob(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "startup-cleanup");

    static async ValueTask CommitCloudStrictAsync(IPantsDatabase database)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }
}
