using System.Text.Json;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Support;
using Cntryl.Pants.Support.Failpoints;
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
            .GetProperty("segments")[0];
        Assert.Equal(1UL, catalog.RootElement.GetProperty("fencing_epoch").GetUInt64());
        Assert.Equal(1UL, publication.GetProperty("epoch").GetUInt64());
        Assert.Equal(3UL, publication.GetProperty("facts").GetProperty("link").GetProperty("last_seq").GetUInt64());
        Assert.Equal(
            "wal/epochs/00000000000000000001/00000000000000000001.wal",
            PantsCloudObjectLayout.WalSegmentObjectKey(publication.GetProperty("epoch").GetUInt64(),
                publication.GetProperty("facts").GetProperty("link").GetProperty("id").GetUInt64()));
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "cloud_store",
            "wal",
            "epochs",
            "00000000000000000001",
            "00000000000000000001.wal")));
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(3UL, metrics.WalCloudDurableSequence);
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        Assert.Single(ledger.Members);
        Assert.Equal(WalSegmentCodec.HeaderSize, new FileInfo(ledger.ActivePath).Length);
    }

    [Fact]
    public async Task ShouldCompleteCloudStrictWriteWhileCompactionWaitsForActorPublication()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new BlockingCompactionOutputFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "database/")
            .WithFlushAfterWalRecordsForTesting(1)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoints));

        await CommitCloudStrictValueAsync(database, "seed/first", "value/first", forceRewrite: true);
        await CommitCloudStrictValueAsync(database, "seed/second", "value/second", forceRewrite: true);

        var compaction = database.Maintenance.CompactAllAsync().AsTask();
        try
        {
            await failpoints.WaitUntilBlockedAsync(TestTimeouts.Expected);
            await CommitCloudStrictValueAsync(database, "during-compaction", "acknowledged")
                .WaitAsync(TestTimeouts.Expected);
        }
        finally
        {
            failpoints.Release();
        }

        await compaction.WaitAsync(TestTimeouts.Expected);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "acknowledged",
            TestBytes.ToText((await reader.GetAsync("during-compaction"u8.ToArray()))!.Value));
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
        using var previousCatalog = JsonDocument.Parse(expected);
        var previousFencingEpoch = previousCatalog.RootElement
            .GetProperty("fencing_epoch")
            .GetUInt64();
        await File.WriteAllBytesAsync(primaryPath, expected.AsMemory(0, expected.Length / 2).ToArray());
        RemoveLocalCache(directory.Path);

        await using (var recovered = await OpenAsync(directory.Path))
        await using (var reader = await recovered.Transactions.BeginAsync(
                         recovered.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(
                "remote/value",
                TestBytes.ToText((await reader.GetAsync("remote/key"u8.ToArray()))!.Value));
        }

        var repairedBytes = await File.ReadAllBytesAsync(primaryPath);
        using var repaired = JsonDocument.Parse(repairedBytes);
        Assert.Equal(JsonValueKind.Array, repaired.RootElement.GetProperty("segments").ValueKind);
        Assert.True(
            repaired.RootElement.GetProperty("fencing_epoch").GetUInt64() > previousFencingEpoch);
        Assert.Equal(repairedBytes, await File.ReadAllBytesAsync(mirrorPath));
    }

    [Fact]
    public async Task ShouldFailClosedWithoutMutatingEitherCatalogCopyWhenBothAreInvalid()
    {
        using var directory = new TemporaryDirectory();
        await CommitCloudStrictAsync(directory.Path);
        RemoveCloudCheckpoints(directory.Path);
        var primaryPath = CatalogPath(directory.Path, "publication-catalog.v1.json");
        var mirrorPath = CatalogPath(directory.Path, "publication-catalog.v1.mirror.json");
        await File.WriteAllBytesAsync(primaryPath, "{ torn"u8.ToArray());
        await File.WriteAllBytesAsync(mirrorPath, "{ also torn"u8.ToArray());
        RemoveLocalCache(directory.Path);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            async () => await OpenAsync(directory.Path));

        Assert.Contains("Both cloud WAL publication catalog copies are unusable", exception.Message, StringComparison.Ordinal);
        Assert.Equal("{ torn"u8.ToArray(), await File.ReadAllBytesAsync(primaryPath));
        Assert.Equal("{ also torn"u8.ToArray(), await File.ReadAllBytesAsync(mirrorPath));
    }

    [Fact]
    public async Task ShouldRejectOpenWhenWalObjectsExistButBothCatalogCopiesAreMissing()
    {
        using var directory = new TemporaryDirectory();
        await CommitCloudStrictAsync(directory.Path);
        RemoveCloudCheckpoints(directory.Path);
        File.Delete(CatalogPath(directory.Path, "publication-catalog.v1.json"));
        File.Delete(CatalogPath(directory.Path, "publication-catalog.v1.mirror.json"));
        var orphanWal = Path.Combine(
            directory.Path,
            "cloud_store",
            "wal",
            "epochs",
            "00000000000000000001",
            "00000000000000000099.wal");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanWal)!);
        await File.WriteAllBytesAsync(orphanWal, [1]);
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

    static async Task CommitCloudStrictValueAsync(IPantsDatabase database, string key, string value, bool forceRewrite = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(value));
        if (forceRewrite)
        {
            // Overlap only empty keyspace so this fixture requires a physical rewrite.
            transaction.DeleteRange("!overlap/a"u8.ToArray(), "!overlap/z"u8.ToArray());
        }
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static string CatalogPath(string root, string name) =>
        Path.Combine(root, "cloud_store", "wal", name);

    static void RemoveCloudCheckpoints(string root)
    {
        var cloudRoot = Path.Combine(root, "cloud_store");
        foreach (var name in new[] { "metadata", "sst" })
        {
            var path = Path.Combine(cloudRoot, name);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

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
        var selected = await CloudMetadataAuthority.ReadCommittedFilesAsync(
            new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")), CancellationToken.None);
        var files = Assert.IsType<Dictionary<string, ReadOnlyMemory<byte>>>(selected);
        Assert.Contains("manifest.snapshot.json", files.Keys);
        Assert.Contains("intent_log.json", files.Keys);
        var manifest = CloudManifestReader.DecodeMetadata(files);
        Assert.Single(manifest.Files);

    }

    [Theory]
    [InlineData("metadata/committed-pointer.v1")]
    [InlineData("midge_primary_lease.json")]
    public async Task ShouldRejectReopenGivenInitializedSimulatedAuthorityLostARequiredObject(string objectKey)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            transaction.Put("durable"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        File.Delete(Path.Combine(directory.Path, "cloud_store", objectKey.Replace('/', Path.DirectorySeparatorChar)));
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());
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
