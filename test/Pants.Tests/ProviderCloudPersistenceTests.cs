namespace Pants.Tests;

public sealed class ProviderCloudPersistenceTests
{
    [Fact]
    public async Task ShouldFenceWalCatalogToNewLeaseBeforeAcceptingWrites()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);

        await using (var first = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(firstCache.Path, CreateAzureLocation()),
                         dependencies))
        {
            Assert.True(first.IsPrimaryLeaseHealthy);
        }

        await using (var second = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(secondCache.Path, CreateAzureLocation()),
                         dependencies))
        {
            Assert.True(second.IsPrimaryLeaseHealthy);
        }

        using var catalog = System.Text.Json.JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        Assert.Equal(2UL, catalog.RootElement.GetProperty("fencing_epoch").GetUInt64());
    }

    [Fact]
    public async Task ShouldFenceBestEffortWriteBeforeLocalMutationGivenExpiredCloudLease()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation())
            .WithTtlClock(clock);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(cloudHttpClient: client));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        clock.UtcNow += TimeSpan.FromMinutes(1);

        await Assert.ThrowsAsync<PantsFencedException>(
            () => transaction.CommitAsync(PantsWriteOptions.BestEffort).AsTask());

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.CurrentSequence);
    }

    [Fact]
    public async Task ShouldFailCloudStrictCommitGivenWalUploadFailure()
    {
        using var cache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(cache.Path, location),
                         dependencies))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            handler.FailWalWrites = true;

            await Assert.ThrowsAsync<PantsInternalException>(
                () => transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
        }

        handler.FailWalWrites = false;
        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(replacementCache.Path, location),
            dependencies);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
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

        for (var attempt = 0; attempt < 1000 && handler.FailedWalWriteAttempts == 0; attempt++)
        {
            await Task.Yield();
        }

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("key"u8.ToArray()))));
        Assert.True(handler.FailedWalWriteAttempts > 0);
        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.True(metrics.WalCloudDurableSequence < metrics.CurrentSequence);

        handler.FailWalWrites = false;
    }

    [Fact]
    public async Task ShouldResumeFailedCloudAsyncUploadFromRecoveredLocalWal()
    {
        using var cache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation());
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            handler.FailWalWrites = true;
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        handler.FailWalWrites = false;
        await using (var resumed = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            var metrics = await resumed.GetRuntimeMetricsAsync();
            Assert.True(metrics.WalCloudDurableSequence >= metrics.CurrentSequence);
            Assert.True(handler.ContainsObjectPath("/wal/epochs/"));
            await resumed.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(replacementCache.Path, CreateAzureLocation()),
            dependencies);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("key"u8.ToArray()))));
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

    [Fact]
    public async Task ShouldRecoverFlushedSstThroughAzureProviderGivenEmptyCache()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(firstCache.Path, CreateAzureLocation()),
                         dependencies))
        {
            await using (var transaction = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await database.FlushAsync(database.DefaultColumnFamily);
        }

        Assert.True(handler.ContainsObjectPath("/sst/"));
        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(secondCache.Path, CreateAzureLocation()),
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
