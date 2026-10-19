using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsProviderCloudStartupCleanupTests
{
    [Fact]
    public async Task ShouldReportColdStartReadinessAndMaintenancePeakWithinProductionMemoryProfile()
    {
        const long ProductionMemoryProfileBytes = 512L * 1024 * 1024;
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var measurements = new List<StartupPhaseMeasurement>();
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.Mixed)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(ProductionMemoryProfileBytes))
            .WithBackgroundCompaction(false);
        var dependencies = new RuntimeDependencies(
            cloudHttpClient: client,
            startupPhaseMeasurement: measurements.Add);
        var resolved = PantsOpenOptionsResolver.Resolve(options);

        await using var database = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(ProductionMemoryProfileBytes, resolved.MemoryBudgetBytes);
        Assert.Equal(ProductionMemoryProfileBytes, metrics.MemoryBudgetBytes);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        Assert.True(database.Cloud!.IsPrimaryLeaseHealthy);
        Assert.Contains(StartupPhase.CloudControlHydration, measurements.Select(static item => item.Phase));
        Assert.Contains(StartupPhase.WalReplay, measurements.Select(static item => item.Phase));
        Assert.Contains(StartupPhase.ServiceStartup, measurements.Select(static item => item.Phase));
        Assert.Equal(resolved.CompactionMemoryPoolBytes, metrics.MaintenanceMemoryCapacityBytes);
        Assert.InRange(
            metrics.MaintenanceMemoryPeakBytes,
            1,
            metrics.MaintenanceMemoryCapacityBytes);
        Assert.InRange(
            metrics.MaintenanceMemoryUsedBytes,
            0,
            metrics.MaintenanceMemoryCapacityBytes);
        Assert.Equal(metrics.CompactionBufferCapacityBytes, metrics.MaintenanceMemoryCapacityBytes);
    }

    [Fact]
    public async Task ShouldReleaseCloudLeaseAfterStrictHydrationFailure()
    {
        using var initialCache = new TemporaryDirectory();
        using var recoveryCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
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
            (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).Health);
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
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, location)
            .WithBackgroundCompaction(false);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask());

        handler.AcknowledgeWalCatalogWritesWithoutPersisting = false;
        await using var corrected = await PantsDatabase.OpenForTestingAsync(
            options,
            dependencies);
        Assert.True(corrected.Cloud!.IsPrimaryLeaseHealthy);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string cachePath,
        PantsCloudStorageLocation location,
        RuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cachePath, location)
                .WithBackgroundCompaction(false),
            dependencies);

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "startup-cleanup");

    static async ValueTask CommitCloudStrictAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }
}
