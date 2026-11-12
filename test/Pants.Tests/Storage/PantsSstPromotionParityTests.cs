using System.Security.Cryptography;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstPromotionParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPromoteDisjointFilesWithoutRewritingAndLeaveTheInnerLevelUnchangedOnASecondDrain(bool cloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (cloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "sst-promotion/")
                : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        string[] originals;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            foreach (var key in new[] { "a", "m", "z" })
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                writer.Put(TestBytes.FromString(key), TestBytes.FromString($"value-{key}"));
                await writer.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
            var before = await database.Diagnostics.GetStorageLayoutAsync();
            var files = Assert.Single(before.Levels).Files;
            Assert.Equal(0, Assert.Single(before.Levels).Level);
            Assert.Equal(3, files.Count);
            originals = files.Select(static file => file.Name).Order(StringComparer.Ordinal).ToArray();
            var bytes = originals.ToDictionary(name => name,
                name => SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.Path, "sst", name))));
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using var pinned = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal(3, (await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts);

            await database.Maintenance.CompactAllAsync();

            var promoted = await database.Diagnostics.GetStorageLayoutAsync();
            var inner = Assert.Single(promoted.Levels);
            Assert.Equal(1, inner.Level);
            Assert.Equal(originals, inner.Files.Select(static file => file.Name).Order(StringComparer.Ordinal));
            foreach (var file in inner.Files)
            {
                Assert.Equal(bytes[file.Name], SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.Path, "sst", file.Name))));
                var original = files.Single(candidate => candidate.Name == file.Name);
                Assert.Equal(original.SizeBytes, file.SizeBytes);
                Assert.Equal(original.SmallestSequence, file.SmallestSequence);
                Assert.Equal(original.LargestSequence, file.LargestSequence);
                Assert.Equal(original.SmallestKey!.Value.ToArray(), file.SmallestKey!.Value.ToArray());
                Assert.Equal(original.LargestKey!.Value.ToArray(), file.LargestKey!.Value.ToArray());
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(metrics.SstBytesWrittenTotal, after.SstBytesWrittenTotal);
            Assert.Equal(metrics.CompactionBytesRewritten, after.CompactionBytesRewritten);
            Assert.Equal(metrics.CompactionsRun, after.CompactionsRun);
            Assert.Empty(promoted.ObsoleteFiles);
            await AssertRowsAsync(pinned);

            await database.Maintenance.CompactAllAsync();
            var again = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Equal(1, Assert.Single(again.Levels).Level);
            Assert.Equal(originals, Assert.Single(again.Levels).Files.Select(static file => file.Name).Order(StringComparer.Ordinal));
            Assert.Equal(after.SstBytesWrittenTotal, (await database.Diagnostics.GetRuntimeMetricsAsync()).SstBytesWrittenTotal);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovered = await reopened.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal(1, Assert.Single(recovered.Levels).Level);
        Assert.Equal(originals, Assert.Single(recovered.Levels).Files.Select(static file => file.Name).Order(StringComparer.Ordinal));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await AssertRowsAsync(reader);
    }

    [Fact]
    public async Task ShouldFlushDisjointGenerationsUnderTheNativeTinyBudgetWithoutRewritingThem()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 64; index++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                writer.Put(TestBytes.FromString($"key-{index:000}"), new byte[1024]);
                await writer.CommitAsync(PantsWriteOptions.Buffered);
                if (index % 10 == 0)
                {
                    await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
                }
            }
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await database.Maintenance.CompactAllAsync();
            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Equal(1, Assert.Single(layout.Levels).Level);
            Assert.Equal(8, Assert.Single(layout.Levels).Files.Count);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.SstBytesWrittenTotal, after.SstBytesWrittenTotal);
            Assert.Equal(0, after.CompactionsRun);
            Assert.Equal(0, after.CompactionBytesRewritten);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options.WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024)));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var seen = 0;
        await foreach (var row in scan)
        {
            Assert.Equal($"key-{seen:000}", TestBytes.ToText(row.Key));
            Assert.Equal(1024, row.Value.Length);
            Assert.DoesNotContain(row.Value.ToArray(), static value => value != 0);
            seen++;
        }
        Assert.Equal(64, seen);
    }

    [Fact]
    public async Task ShouldPromotePublishedHybridFilesWithoutReservingOutputSpaceOrHydratingInputs()
    {
        using var directory = new TemporaryDirectory();
        const long localBudget = 128 * 1024;
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "promotion-headroom/")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithTransactionMemoryPool(1024)
            .WithSimulatedCloudLocalStorageBudget(localBudget)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        foreach (var key in new[] { "a", "m", "z" })
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put(TestBytes.FromString(key), TestBytes.FromString($"value-{key}"));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
        var originals = (await database.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files)
            .Select(static file => file.Name).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"))
        {
            File.Delete(path);
        }
        await using var hog = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        var held = 0L;
        for (var index = 0; index < 100 && held <= localBudget / 2; index++)
        {
            hog.Put(TestBytes.FromString($"held-{index:000}"), new byte[4096]);
            held = (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget!.Usage.TransactionSpillBytes;
        }
        Assert.True(held > localBudget / 2);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await database.Maintenance.CompactAllAsync();

        var level = Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels);
        Assert.Equal(1, level.Level);
        Assert.Equal(originals, level.Files.Select(static file => file.Name).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.SstBytesWrittenTotal, after.SstBytesWrittenTotal);
        Assert.Equal(0, after.HybridStorageBudget!.Usage.CompactionStagingReservedBytes);
        Assert.Equal(held, after.HybridStorageBudget.Usage.TransactionSpillBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRewriteOverlappingGenerationsWhileRetainingAnOlderSnapshot(bool cloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (cloud ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "overlapping-promotion/") : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        await PutAsync(database, cloud, "same", "old");
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var pinned = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await PutAsync(database, cloud, "same", "new");
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var originals = (await database.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files).Select(static file => file.Name).ToArray();
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await database.Maintenance.CompactAllAsync();

        var files = Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).Files;
        Assert.DoesNotContain(files, file => originals.Contains(file.Name, StringComparer.Ordinal));
        Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CompactionBytesRewritten > before.CompactionBytesRewritten);
        Assert.Equal("old", TestBytes.ToText((await pinned.GetAsync("same"u8.ToArray()))!.Value));
        await using var current = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("new", TestBytes.ToText((await current.GetAsync("same"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldRewriteAFileLargerThanTheAcceptedOutputTarget()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(TargetSstSizeBytes: 1024, BackgroundEnabled: false)));
        var value = new byte[4096];
        new Random(812).NextBytes(value);
        await using (var writer = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            writer.Put("large"u8.ToArray(), value);
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var original = Assert.Single(Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).Files);
        Assert.True(original.SizeBytes > 1024);

        await database.Maintenance.CompactAllAsync();

        var rewritten = Assert.Single(Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).Files);
        Assert.NotEqual(original.Name, rewritten.Name);
        Assert.Equal(1, rewritten.Level);
        Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CompactionsRun > 0);
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(value, (await reader.GetAsync("large"u8.ToArray()))!.Value.ToArray());
    }

    [Fact]
    public async Task ShouldRewriteAnOtherwiseReadableFileWithUntrustedManifestBounds()
    {
        using var directory = new TemporaryDirectory();
        const string name = "000000_00_00000000000000000001.sst";
        var bytes = SstCodec.Encode([new SstEntry("legacy"u8.ToArray(), "value"u8.ToArray(), 7, null, false)], [], PantsPerformanceGoal.Latency);
        Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "sst", name), bytes);
        var manifest = new ManifestState
        {
            LastPersistedSequence = 7,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 2 },
            FlushedThrough = new Dictionary<uint, ulong> { [0] = 7 },
            Files = [new FileMeta
            {
                Name = name, SizeBytes = checked((ulong)bytes.Length), Level = 0, ColumnFamilyId = 0, SstSequence = 1,
                ContentCrc32C = DiskFormat.Crc32C(bytes), SmallestKey = "legacy"u8.ToArray(), LargestKey = "legacy"u8.ToArray(),
                KeyBoundsComplete = false, SmallestSequence = 7, LargestSequence = 7
            }]
        };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(manifest,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        await database.Maintenance.CompactAllAsync();

        var rewritten = Assert.Single(Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).Files);
        Assert.NotEqual(name, rewritten.Name);
        Assert.Equal(1, rewritten.Level);
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText((await reader.GetAsync("legacy"u8.ToArray()))!.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldPreserveCapturedPhysicalInputsWhenMovePublicationFails(bool cloud, bool afterJournal)
    {
        using var directory = new TemporaryDirectory();
        var options = (cloud ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "failed-move/") : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        var failure = new MovePublicationFailure(afterJournal ? Failpoint.AfterCompactionManifestPublish : Failpoint.BeforeCompactionManifestPublish);
        string[] originals;
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failure)))
        {
            foreach (var key in new[] { "a", "m", "z" })
            {
                await PutAsync(database, cloud, key, $"value-{key}");
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
            originals = (await database.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files).Select(static file => file.Name).Order(StringComparer.Ordinal).ToArray();
            var hashes = originals.ToDictionary(name => name, name => SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.Path, "sst", name))));
            failure.Arm();

            var error = await Record.ExceptionAsync(() => database.Maintenance.CompactAllAsync().AsTask());

            Assert.NotNull(error);
            Assert.True(error is IOException or PantsIOException, error.ToString());
            Assert.Equal(1, failure.Hits);
            foreach (var name in originals)
            {
                Assert.Equal(hashes[name], SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.Path, "sst", name))));
            }
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(originals, (await reopened.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files).Select(static file => file.Name).Order(StringComparer.Ordinal));
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await AssertRowsAsync(reader);
        await reopened.Maintenance.CompactAllAsync();
        Assert.Equal(originals, Assert.Single((await reopened.Diagnostics.GetStorageLayoutAsync()).Levels).Files.Select(static file => file.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ShouldPublishTheSameRemoteSstIdentitiesAndRecoverPromotedLevelsFromAnEmptyProviderCache()
    {
        using var directory = new TemporaryDirectory();
        using var cold = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")), "provider-promotion");
        var options = PantsOpenOptions.Cloud(directory.Path, location).WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        string[] originals;
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            foreach (var key in new[] { "a", "m", "z" })
            {
                await PutAsync(database, true, key, $"value-{key}");
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
            originals = handler.GetObjectPaths("/sst/");
            var hashes = originals.ToDictionary(name => name, name => SHA256.HashData(handler.GetObjectBytes(name)));
            foreach (var path in Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"))
            {
                File.Delete(path);
            }
            var rangeReads = handler.SstRangeReads;
            var fullReads = handler.SstFullReads;

            await database.Maintenance.CompactAllAsync();

            Assert.Equal(1, Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels).Level);
            Assert.Equal(originals, handler.GetObjectPaths("/sst/"));
            Assert.Equal(rangeReads, handler.SstRangeReads);
            Assert.Equal(fullReads, handler.SstFullReads);
            foreach (var name in originals)
            {
                Assert.Equal(hashes[name], SHA256.HashData(handler.GetObjectBytes(name)));
            }
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(PantsOpenOptions.Cloud(cold.Path, location)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024)).WithBackgroundCompaction(false), dependencies);
        Assert.Equal(1, Assert.Single((await reopened.Diagnostics.GetStorageLayoutAsync()).Levels).Level);
        Assert.Equal(originals, handler.GetObjectPaths("/sst/"));
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await AssertRowsAsync(reader);
    }

    static async Task PutAsync(IPantsDatabase database, bool cloud, string key, string value)
    {
        await using var writer = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        writer.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await writer.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    sealed class MovePublicationFailure(Failpoint target) : IFailpointHandler
    {
        bool _armed;
        public int Hits { get; private set; }
        public void Arm() => _armed = true;
        public void Hit(Failpoint point)
        {
            if (_armed && point == target)
            {
                _armed = false;
                Hits++;
                throw new IOException("Injected logical move publication failure.");
            }
        }
    }

    static async Task AssertRowsAsync(IPantsTransaction reader)
    {
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var rows = new List<(string Key, string Value)>();
        await foreach (var row in scan)
        {
            rows.Add((TestBytes.ToText(row.Key), TestBytes.ToText(row.Value)));
        }
        Assert.Equal([("a", "value-a"), ("m", "value-m"), ("z", "value-z")], rows);
    }
}
