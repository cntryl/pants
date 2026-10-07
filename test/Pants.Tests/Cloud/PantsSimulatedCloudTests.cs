using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsSimulatedCloudTests
{
    [Fact]
    public async Task ShouldPublishCloudStrictCommitThroughEpochScopedCatalog()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
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
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(3, metrics.WalCloudDurableSequence);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
    }

    [Fact]
    public async Task ShouldRecoverCloudCommitAfterLosingLocalCache()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("remote/key"u8.ToArray(), "remote/value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        RemoveLocalCache(directory.Path);

        await using var recovered = await OpenAsync(directory.Path);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("remote/key"u8.ToArray());
        Assert.Equal("remote/value", TestBytes.ToText(value!.Value));
    }

    [Fact]
    public async Task ShouldConvergeCatalogMirrorAndRecoverFromTornPrimaryAfterLosingLocalCache()
    {
        using var directory = new TemporaryDirectory();
        await CommitCloudStrictAsync(directory.Path);
        var primaryPath = CatalogPath(directory.Path, "publication-catalog.v1.json");
        var mirrorPath = CatalogPath(directory.Path, "publication-catalog.v1.mirror.json");
        var expected = await File.ReadAllBytesAsync(primaryPath);
        Assert.Equal(expected, await File.ReadAllBytesAsync(mirrorPath));
        await File.WriteAllBytesAsync(primaryPath, expected.AsMemory(0, expected.Length / 2).ToArray());
        RemoveLocalCache(directory.Path);

        await using var recovered = await OpenAsync(directory.Path);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("remote/value", TestBytes.ToText((await reader.GetAsync("remote/key"u8.ToArray()))!.Value));
        using var repaired = JsonDocument.Parse(await File.ReadAllBytesAsync(primaryPath));
        Assert.True(repaired.RootElement.GetProperty("segments").TryGetProperty("1", out _));
        Assert.Equal(
            await File.ReadAllBytesAsync(primaryPath),
            await File.ReadAllBytesAsync(mirrorPath));
    }

    [Fact]
    public async Task ShouldFailClosedWithoutMutatingEitherCatalogCopyWhenBothAreInvalid()
    {
        using var directory = new TemporaryDirectory();
        await CommitCloudStrictAsync(directory.Path);
        var primaryPath = CatalogPath(directory.Path, "publication-catalog.v1.json");
        var mirrorPath = CatalogPath(directory.Path, "publication-catalog.v1.mirror.json");
        await File.WriteAllBytesAsync(primaryPath, "{ torn"u8.ToArray());
        await File.WriteAllBytesAsync(mirrorPath, "{ also torn"u8.ToArray());
        RemoveLocalCache(directory.Path);

        await Assert.ThrowsAnyAsync<PantsException>(async () => await OpenAsync(directory.Path));

        Assert.Equal("{ torn"u8.ToArray(), await File.ReadAllBytesAsync(primaryPath));
        Assert.Equal("{ also torn"u8.ToArray(), await File.ReadAllBytesAsync(mirrorPath));
    }

    [Fact]
    public async Task ShouldRejectOpenWhenWalObjectsExistButBothCatalogCopiesAreMissing()
    {
        using var directory = new TemporaryDirectory();
        await CommitCloudStrictAsync(directory.Path);
        File.Delete(CatalogPath(directory.Path, "publication-catalog.v1.json"));
        File.Delete(CatalogPath(directory.Path, "publication-catalog.v1.mirror.json"));
        RemoveLocalCache(directory.Path);

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(
            async () => await OpenAsync(directory.Path));

        Assert.Contains("publication-catalog.v1.json", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CatalogPath(directory.Path, "publication-catalog.v1.json")));
    }

    static async Task CommitCloudStrictAsync(string path)
    {
        await using var database = await OpenAsync(path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("remote/key"u8.ToArray(), "remote/value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static string CatalogPath(string root, string name) =>
        Path.Combine(root, "cloud_store", "wal", name);

    [Fact]
    public async Task ShouldMirrorFlushedSstAndRecoveryMetadata()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("flush/key"u8.ToArray(), "flush/value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

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
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
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
