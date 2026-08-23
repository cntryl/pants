using System.Text.Json;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsSimulatedCloudTests
{
    [Fact]
    public async Task ShouldPublishCloudStrictCommitThroughEpochScopedCatalog()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("cloud/key"u8.ToArray(), "cloud/value"u8.ToArray());

        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);

        var catalogPath = Path.Combine(
            directory.Path,
            "cloud_store",
            "wal",
            "publication-catalog.v1.json");
        using var catalog = JsonDocument.Parse(await File.ReadAllBytesAsync(catalogPath));
        var publication = catalog.RootElement
            .GetProperty("segments")
            .GetProperty("1");
        Assert.Equal(1UL, catalog.RootElement.GetProperty("fencing_epoch").GetUInt64());
        Assert.Equal(1UL, publication.GetProperty("writer_epoch").GetUInt64());
        Assert.Equal(3UL, publication.GetProperty("max_sequence").GetUInt64());
        Assert.Equal(
            "wal/epochs/00000000000000000001/00000000000000000001.wal",
            publication.GetProperty("object_key").GetString());
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "cloud_store",
            "wal",
            "epochs",
            "00000000000000000001",
            "00000000000000000001.wal")));
        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(3, metrics.WalCloudDurableSequence);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
    }

    [Fact]
    public async Task ShouldRecoverCloudCommitAfterLosingLocalCache()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("remote/key"u8.ToArray(), "remote/value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        RemoveLocalCache(directory.Path);

        await using var recovered = await OpenAsync(directory.Path);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("remote/key"u8.ToArray());
        Assert.Equal("remote/value", TestBytes.ToText(value!.Value));
    }

    [Fact]
    public async Task ShouldMirrorFlushedSstAndRecoveryMetadata()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("flush/key"u8.ToArray(), "flush/value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "cloud_store", "sst"), "*.sst"));
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "cloud_store",
            "metadata",
            "manifest.snapshot.json")));
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "cloud_store",
            "metadata",
            "intent_log.json")));
    }

    [Fact]
    public async Task ShouldAllowReadOnlyCommitWithoutACloudWriteDurabilityPolicy()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.SimulatedCloud(path, "pants-tests", "database/"));

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
}
