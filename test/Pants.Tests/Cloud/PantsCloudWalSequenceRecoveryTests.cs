using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pants.Tests;

public sealed class PantsCloudWalSequenceRecoveryTests
{
    [Fact]
    public async Task ShouldAdvanceProviderWalSegmentIdsAcrossRepeatedCacheLossRecovery()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var thirdCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateProviderLocation();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: client);

        await using (var database = await OpenProviderAsync(
                         firstCache.Path,
                         location,
                         dependencies))
        {
            await CommitAsync(database, "first", "one");
        }

        RegressProviderManifestWalSequence(handler);
        await using (var recovered = await OpenProviderAsync(
                         secondCache.Path,
                         location,
                         dependencies))
        {
            await AssertValueAsync(recovered, "first", "one");
            Assert.Equal(2UL, ReadManifestNextWalSequence(secondCache.Path));
            await CommitAsync(recovered, "second", "two");
        }

        RegressProviderManifestWalSequence(handler);
        await using (var recoveredAgain = await OpenProviderAsync(
                         thirdCache.Path,
                         location,
                         dependencies))
        {
            await AssertValueAsync(recoveredAgain, "first", "one");
            await AssertValueAsync(recoveredAgain, "second", "two");
            Assert.Equal(3UL, ReadManifestNextWalSequence(thirdCache.Path));
        }

        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        var segments = catalog.RootElement.GetProperty("segments");
        Assert.True(segments.TryGetProperty("1", out _));
        Assert.True(segments.TryGetProperty("2", out _));
    }

    [Fact]
    public async Task ShouldAdvanceSimulatedWalSegmentIdsAcrossRepeatedCacheLossRecovery()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateSimulatedOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, "first", "one");
        }

        RegressSimulatedManifestWalSequence(directory.Path);
        RemoveSimulatedLocalCache(directory.Path);
        await using (var recovered = await PantsDatabase.OpenAsync(options))
        {
            await AssertValueAsync(recovered, "first", "one");
            Assert.Equal(2UL, ReadManifestNextWalSequence(directory.Path));
            await CommitAsync(recovered, "second", "two");
        }

        RegressSimulatedManifestWalSequence(directory.Path);
        RemoveSimulatedLocalCache(directory.Path);
        await using (var recoveredAgain = await PantsDatabase.OpenAsync(options))
        {
            await AssertValueAsync(recoveredAgain, "first", "one");
            await AssertValueAsync(recoveredAgain, "second", "two");
            Assert.Equal(3UL, ReadManifestNextWalSequence(directory.Path));
        }

        using var catalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            directory.Path,
            "cloud_store",
            "wal",
            "publication-catalog.v1.json")));
        var segments = catalog.RootElement.GetProperty("segments");
        Assert.True(segments.TryGetProperty("1", out _));
        Assert.True(segments.TryGetProperty("2", out _));
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        string key,
        string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async ValueTask AssertValueAsync(
        IPantsDatabase database,
        string key,
        string expected)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = Assert.IsType<ReadOnlyMemory<byte>>(
            await transaction.GetAsync(TestBytes.FromString(key)));
        Assert.Equal(expected, TestBytes.ToText(value));
    }

    static ValueTask<IPantsDatabase> OpenProviderAsync(
        string cachePath,
        PantsCloudStorageLocation location,
        PantsRuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cachePath, location)
                .WithBackgroundCompaction(false),
            dependencies);

    static PantsCloudStorageLocation CreateProviderLocation() => new(
        new PantsCloudProviderConfiguration.AzureBlob(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "wal-sequence-recovery");

    static PantsOpenOptions CreateSimulatedOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "wal-sequence-recovery/")
            .WithBackgroundCompaction(false);

    static void RegressProviderManifestWalSequence(InMemoryAzureBlobHandler handler)
    {
        foreach (var path in handler.GetObjectPaths("/metadata/manifest")
                     .Where(static path => path.EndsWith(".json", StringComparison.Ordinal)))
        {
            handler.ReplaceObjectText(
                path,
                SetNextWalSequence(handler.GetObjectText(path), nextWalSequence: 1));
        }
    }

    static void RegressSimulatedManifestWalSequence(string root)
    {
        var metadataDirectory = Path.Combine(root, "cloud_store", "metadata");
        foreach (var path in Directory.EnumerateFiles(
                     metadataDirectory,
                     "manifest*.json",
                     SearchOption.TopDirectoryOnly))
        {
            File.WriteAllText(
                path,
                SetNextWalSequence(File.ReadAllText(path), nextWalSequence: 1));
        }
    }

    static string SetNextWalSequence(string manifestJson, ulong nextWalSequence)
    {
        var manifest = JsonNode.Parse(manifestJson)?.AsObject() ??
            throw new InvalidOperationException("The cloud manifest was empty.");
        manifest["next_wal_seq"] = nextWalSequence;
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static ulong ReadManifestNextWalSequence(string root)
    {
        var snapshotPath = Path.Combine(root, "manifest.snapshot.json");
        var path = File.Exists(snapshotPath)
            ? snapshotPath
            : Path.Combine(root, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        return manifest.RootElement.GetProperty("next_wal_seq").GetUInt64();
    }

    static void RemoveSimulatedLocalCache(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(path) == "cloud_store")
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }
}
