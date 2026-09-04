using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudWalPruningTests
{
    [Fact]
    public async Task ShouldConditionallyDeleteProviderWalAfterCatalogRetiresSegment()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, location).WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("provider"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        Assert.True(handler.ContainsObjectPath("/wal/epochs/"));

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.False(handler.ContainsObjectPath("/wal/epochs/"));
        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        Assert.Empty(catalog.RootElement.GetProperty("segments").EnumerateObject());
    }

    [Fact]
    public async Task ShouldPreserveProviderWalWhenUnflushedColumnFamilyStillDependsOnItGivenPartialGc()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateProviderLocation())
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            var other = await database.ColumnFamilies.CreateAsync("provider-other");
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "provider-default-first"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                other,
                "provider-other-retained"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "provider-default-last"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        ResetDirectory(Path.Combine(cache.Path, "wal"));
        await using (var reopened = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            await reopened.Maintenance.FlushAsync(reopened.ColumnFamilies.DefaultFamily);
            Assert.NotEmpty(ReadProviderCatalogSegments(handler));
            await reopened.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        ResetDirectory(Path.Combine(cache.Path, "wal"));
        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        var recoveredOther = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await recovered.ColumnFamilies.GetAsync("provider-other"));
        await using var reader = await recovered.Transactions.BeginAsync(
            recoveredOther,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("provider-other-retained"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRetainProviderWalGivenAcknowledgedSstUploadHasNoObject()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateProviderLocation())
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));
        await CommitProviderValueAsync(database);
        handler.AcknowledgeSstWritesWithoutPersisting = true;

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.True(handler.ContainsObjectPath("/wal/epochs/"));
        Assert.NotEmpty(ReadProviderCatalogSegments(handler));
        handler.AcknowledgeSstWritesWithoutPersisting = false;
    }

    [Fact]
    public async Task ShouldRetainProviderWalGivenAcknowledgedMetadataCasHasNoObject()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateProviderLocation())
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));
        await CommitProviderValueAsync(database);
        handler.AcknowledgeMetadataWritesWithoutPersisting = true;

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.True(handler.ContainsObjectPath("/wal/epochs/"));
        Assert.NotEmpty(ReadProviderCatalogSegments(handler));
        handler.AcknowledgeMetadataWritesWithoutPersisting = false;
    }

    [Fact]
    public async Task ShouldRetainProviderWalGivenCatalogRetirementHasNoReadback()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateProviderLocation())
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));
        await CommitProviderValueAsync(database);
        handler.AcknowledgeWalCatalogWritesWithoutPersisting = true;

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.True(handler.ContainsObjectPath("/wal/epochs/"));
        Assert.NotEmpty(ReadProviderCatalogSegments(handler));
        handler.AcknowledgeWalCatalogWritesWithoutPersisting = false;
    }

    [Fact]
    public async Task ShouldPruneRemoteWalAfterPublishedSstCoversItsSequence()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("covered"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            Assert.NotEmpty(RemoteWalPaths(directory.Path));

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

            Assert.Empty(ReadCatalogSegments(directory.Path));
            Assert.Empty(RemoteWalPaths(directory.Path));
        }

        RemoveLocalCache(directory.Path);
        await using var recovered = await PantsDatabase.OpenAsync(options);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("covered"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldIgnoreReintroducedWalObjectAfterCatalogRetiresSegment()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        byte[] retiredBytes;
        string retiredRelativePath;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("retired"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            var retiredPath = Assert.Single(RemoteWalPaths(directory.Path));
            retiredBytes = await File.ReadAllBytesAsync(retiredPath);
            retiredRelativePath = Path.GetRelativePath(CloudRoot(directory.Path), retiredPath);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var restoredPath = Path.Combine(CloudRoot(directory.Path), retiredRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        await File.WriteAllBytesAsync(restoredPath, retiredBytes);
        RemoveLocalCache(directory.Path);

        await using var recovered = await PantsDatabase.OpenAsync(options);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("retired"u8.ToArray()))));
        Assert.Single(RemoteWalPaths(directory.Path));
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenAuthoritativeRemoteSstMissingWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "missing-sst"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(CloudRoot(directory.Path), "sst"),
                     "*.sst"))
        {
            File.Delete(path);
        }

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => PantsDatabase.OpenAsync(options).AsTask());
    }

    [Fact]
    public async Task ShouldPreserveRemoteWalWhenUnflushedColumnFamilyStillDependsOnItGivenPartialGc()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var other = await database.ColumnFamilies.CreateAsync("other");
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "default-first"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                other,
                "other-retained"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "default-last"u8.ToArray(),
                PantsWriteOptions.CloudStrict);

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

            Assert.NotEmpty(RemoteWalPaths(directory.Path));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        ResetDirectory(Path.Combine(directory.Path, "wal"));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var otherFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("other"));
        await using var reader = await reopened.Transactions.BeginAsync(
            otherFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("other-retained"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRecoverDeleteRangeGivenRemoteWalOnlyWhenLocalCacheIsLost()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "range-10"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "range-15"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "range-25"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.DeleteRange("range-10"u8.ToArray(), "range-20"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            Assert.Empty(RemoteWalPaths(directory.Path));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        ResetDirectory(Path.Combine(directory.Path, "wal"));
        ResetDirectory(Path.Combine(directory.Path, "sst"));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Null(await reader.GetAsync("range-10"u8.ToArray()));
        Assert.Null(await reader.GetAsync("range-15"u8.ToArray()));
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("range-25"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRecoverPartialRemoteWalCleanupGivenMixedFlushStateWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "covered"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            Assert.Empty(RemoteWalPaths(directory.Path));

            await CommitValueAsync(
                database,
                database.ColumnFamilies.DefaultFamily,
                "retained"u8.ToArray(),
                PantsWriteOptions.CloudStrict);
            Assert.NotEmpty(RemoteWalPaths(directory.Path));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        ResetDirectory(Path.Combine(directory.Path, "wal"));
        ResetDirectory(Path.Combine(directory.Path, "sst"));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("covered"u8.ToArray()))));
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("retained"u8.ToArray()))));
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "wal-pruning/")
            .WithBackgroundCompaction(false);

    static PantsCloudStorageLocation CreateProviderLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "database");

    static async Task CommitProviderValueAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("provider-proof"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task CommitValueAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        ReadOnlyMemory<byte> key,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }

    static JsonElement[] ReadProviderCatalogSegments(InMemoryAzureBlobHandler handler)
    {
        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        return catalog.RootElement.GetProperty("segments").EnumerateObject()
            .Select(static property => property.Value.Clone())
            .ToArray();
    }

    static string CloudRoot(string root) => Path.Combine(root, "cloud_store");

    static string[] RemoteWalPaths(string root) => Directory
        .EnumerateFiles(Path.Combine(CloudRoot(root), "wal"), "*.wal", SearchOption.AllDirectories)
        .ToArray();

    static JsonElement[] ReadCatalogSegments(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            CloudRoot(root),
            "wal",
            "publication-catalog.v1.json")));
        return document.RootElement.GetProperty("segments").EnumerateObject()
            .Select(static property => property.Value.Clone())
            .ToArray();
    }

    static void RemoveLocalCache(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(path) == "cloud_store")
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
    }
}
