namespace Pants.Tests;

public sealed class PantsCloudCompactionFailureTests
{
    [Fact]
    public async Task ShouldNotUploadRemoteCompactionOutputWhenIntentSaveFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CloudCompactionFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoints);
        await SeedCompactionInputsAsync(database, database.DefaultColumnFamily, "intent-first");
        var initialRemoteCount = RemoteSsts(directory.Path).Length;
        failpoints.Arm(PantsFailpoint.BeforeIntentLogReplace);

        await Assert.ThrowsAsync<PantsIOException>(
            () => database.CompactAllAsync().AsTask());

        Assert.Equal(4, initialRemoteCount);
        Assert.Equal(initialRemoteCount, RemoteSsts(directory.Path).Length);
        Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).CompactionFailures);
    }

    [Fact]
    public async Task ShouldRemoveRemoteCompactionOrphanOnReopenWhenManifestBatchFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CloudCompactionFailpointHandler();
        var options = CreateOptions(directory.Path);
        var initialRemoteCount = 0;
        await using (var database = await OpenAsync(options, failpoints))
        {
            await SeedCompactionInputsAsync(database, database.DefaultColumnFamily, "remote-orphan");
            initialRemoteCount = RemoteSsts(directory.Path).Length;
            failpoints.Arm(PantsFailpoint.BeforeCompactionManifestPublish);

            await Assert.ThrowsAsync<PantsIOException>(
                () => database.CompactAllAsync().AsTask());

            Assert.Equal(4, initialRemoteCount);
            Assert.Equal(initialRemoteCount + 1, RemoteSsts(directory.Path).Length);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);

        Assert.Equal(initialRemoteCount, RemoteSsts(directory.Path).Length);
        await AssertSeedValuesAsync(reopened, reopened.DefaultColumnFamily, "remote-orphan");
    }

    [Fact]
    public async Task ShouldRemoveRemoteCompactionOrphanWhenColumnFamilyIsDroppedBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CloudCompactionFailpointHandler();
        var options = CreateOptions(directory.Path);
        string orphanPath;
        await using (var database = await OpenAsync(options, failpoints))
        {
            var family = await database.CreateColumnFamilyAsync("drop-after-compaction-failure");
            await SeedCompactionInputsAsync(database, family, "dropped-remote-orphan");
            var initialRemoteNames = RemoteSsts(directory.Path)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal);
            failpoints.Arm(PantsFailpoint.BeforeCompactionManifestPublish);
            await Assert.ThrowsAsync<PantsIOException>(
                () => database.CompactAllAsync().AsTask());
            orphanPath = Assert.Single(
                RemoteSsts(directory.Path),
                path => !initialRemoteNames.Contains(Path.GetFileName(path)));

            await database.DropColumnFamilyAsync(family);

            Assert.True(File.Exists(orphanPath));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);

        Assert.Null(await reopened.GetColumnFamilyAsync("drop-after-compaction-failure"));
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public async Task ShouldRetainReplacedRemoteCompactionOrphanWhenCleanupProofIsStale()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        string orphanPath;
        await using (var database = await OpenAsync(
                         options,
                         new CloudCompactionFailpointHandler()))
        {
            await SeedCompactionInputsAsync(
                database,
                database.DefaultColumnFamily,
                "guarded-remote-orphan");
        }

        var initialRemoteNames = RemoteSsts(directory.Path)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var publicationFailure = new CloudCompactionFailpointHandler();
        publicationFailure.Arm(PantsFailpoint.BeforeCompactionManifestPublish);
        await using (var failingDatabase = await OpenAsync(options, publicationFailure))
        {
            await Assert.ThrowsAsync<PantsIOException>(
                () => failingDatabase.CompactAllAsync().AsTask());

            orphanPath = Assert.Single(
                RemoteSsts(directory.Path),
                path => !initialRemoteNames.Contains(Path.GetFileName(path)));
        }

        var replacement = "replacement must survive stale cleanup proof"u8.ToArray();
        var cleanupFailure = new CloudCompactionFailpointHandler();
        cleanupFailure.ArmCallback(
            PantsFailpoint.BeforeCloudSstGarbageCollectionDelete,
            () => File.WriteAllBytes(orphanPath, replacement));

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(
            () => OpenAsync(options, cleanupFailure).AsTask());

        Assert.Equal(replacement, await File.ReadAllBytesAsync(orphanPath));
        using var lockProbe = new FileStream(
            Path.Combine(directory.Path, "LOCK"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task ShouldRetryFrozenMemtableWhenCloudSstUploadRecovers()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CloudCompactionFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoints);
        await SeedWritesAsync(database, database.DefaultColumnFamily, "cloud-retry");
        failpoints.Arm(PantsFailpoint.BeforeCloudUpload);

        await Assert.ThrowsAsync<PantsIOException>(
            () => database.FlushAsync(database.DefaultColumnFamily).AsTask());

        await WaitForAsync(() => RemoteSsts(directory.Path).Length == 1);
        await AssertSeedValuesAsync(
            database,
            database.DefaultColumnFamily,
            "cloud-retry",
            count: 6);
    }

    static async ValueTask SeedCompactionInputsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string keyPrefix)
    {
        for (var batch = 0; batch < 4; batch++)
        {
            await CommitAsync(database, family, $"{keyPrefix}-{batch}");
            await database.FlushAsync(family);
        }
    }

    static async ValueTask SeedWritesAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string keyPrefix)
    {
        for (var index = 0; index < 6; index++)
        {
            await CommitAsync(database, family, $"{keyPrefix}-{index}");
        }
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }

    static async ValueTask AssertSeedValuesAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string keyPrefix,
        int count = 4)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < count; index++)
        {
            var value = Assert.IsType<ReadOnlyMemory<byte>>(
                await transaction.GetAsync(TestBytes.FromString($"{keyPrefix}-{index}")));
            Assert.Equal("value", TestBytes.ToText(value));
        }
    }

    static async ValueTask WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    static string[] RemoteSsts(string path) => Directory.Exists(CloudSstDirectory(path))
        ? Directory.GetFiles(CloudSstDirectory(path), "*.sst", SearchOption.TopDirectoryOnly)
        : [];

    static string CloudSstDirectory(string path) =>
        Path.Combine(path, "cloud_store", "sst");

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        CloudCompactionFailpointHandler failpoints) =>
        OpenAsync(CreateOptions(path), failpoints);

    static ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CloudCompactionFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "compaction-failures/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: int.MaxValue))
            .WithBackgroundCompaction(false);
}
