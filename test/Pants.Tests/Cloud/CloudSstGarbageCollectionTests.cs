using System.Text;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudSstGarbageCollectionTests
{
    [Fact]
    public async Task ShouldRetainReusableSstNameGivenRemoteManifestFrontier()
    {
        var store = new TestCloudObjectStore();
        const string name = "000000_01_00000000000000000007.sst";
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        Assert.True(await store.PutAsync(
            objectKey,
            "orphan"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        var collector = new CloudSstGarbageCollector(
            store,
            _ => ValueTask.FromResult(new CloudSstRetentionProof(
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<uint, ulong> { [0] = 7 },
                [])),
            () => new HashSet<string>(StringComparer.Ordinal),
            static () => { },
            static _ => ValueTask.CompletedTask,
            NullPantsFailpointHandler.Instance);

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.NotNull(await store.HeadAsync(objectKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldCollectPartialCompactionOutputGivenGuardedMissingSiblingProof()
    {
        var store = new TestCloudObjectStore();
        const string name = "000000_01_00000000000000000007.sst";
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        store.Seed(objectKey, "partial output"u8.ToArray());
        var identity = await store.HeadAsync(objectKey, CancellationToken.None);
        var protectedNames = new HashSet<string>(StringComparer.Ordinal) { name };
        var partialOutputVersions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = identity!.Version
        };
        var collector = new CloudSstGarbageCollector(
            store,
            _ => ValueTask.FromResult(new CloudSstRetentionProof(
                protectedNames,
                partialOutputVersions,
                new Dictionary<uint, ulong> { [0] = 7 },
                [])),
            () => new HashSet<string>(StringComparer.Ordinal),
            static () => { },
            static _ => ValueTask.CompletedTask,
            NullPantsFailpointHandler.Instance);

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.Null(await store.HeadAsync(objectKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRetainPartialCompactionOutputWhenItsObservedIdentityChanges()
    {
        var store = new TestCloudObjectStore();
        const string name = "000000_01_00000000000000000007.sst";
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        store.Seed(objectKey, "partial output"u8.ToArray());
        var identity = await store.HeadAsync(objectKey, CancellationToken.None);
        var protectedNames = new HashSet<string>(StringComparer.Ordinal) { name };
        var partialOutputVersions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = identity!.Version + "-changed"
        };
        var collector = new CloudSstGarbageCollector(
            store,
            _ => ValueTask.FromResult(new CloudSstRetentionProof(
                protectedNames,
                partialOutputVersions,
                new Dictionary<uint, ulong> { [0] = 7 },
                [])),
            () => new HashSet<string>(StringComparer.Ordinal),
            static () => { },
            static _ => ValueTask.CompletedTask,
            NullPantsFailpointHandler.Instance);

        Assert.False(await collector.CollectAsync(CancellationToken.None));

        Assert.NotNull(await store.HeadAsync(objectKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldAdoptRemoteCompactionOrphanGivenSuccessorCacheRecovery()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var finalCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var failpoints = new CloudCompactionFailpointHandler();
        var initialNames = new HashSet<string>(StringComparer.Ordinal);
        var initialMetadata = new Dictionary<string, FileMeta>(StringComparer.Ordinal);
        string orphanPath;
        await using (var database = await OpenProviderAsync(
                         firstCache.Path,
                         client,
                         failpoints))
        {
            await PrepareTwoSstsAsync(database, "successor-adoption");
            initialNames.UnionWith(handler.GetObjectPaths("/sst/"));
            foreach (var file in CloudManifestReader.ReadManifest(firstCache.Path)!.Files)
            {
                initialMetadata.Add(file.Name, file.Clone());
            }
            failpoints.Arm(Failpoint.BeforeCompactionManifestPublish);

            await Assert.ThrowsAsync<PantsIOException>(() => database.Maintenance.CompactAllAsync().AsTask());

            orphanPath = Assert.Single(
                handler.GetObjectPaths("/sst/"),
                path => !initialNames.Contains(path));
        }

        await using (var successor = await OpenProviderAsync(
                         secondCache.Path,
                         client))
        {
            Assert.Contains(
                orphanPath,
                handler.GetObjectPaths("/sst/"),
                StringComparer.Ordinal);

            await successor.Maintenance.CompactAllAsync();

            Assert.Contains(orphanPath, handler.GetObjectPaths("/sst/"), StringComparer.Ordinal);
            var authoritative = Assert.Single((await successor.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files));
            Assert.Equal(Path.GetFileName(orphanPath), authoritative.Name);
            var retired = CloudManifestReader.ReadManifest(secondCache.Path)!.RetiredSsts;
            foreach (var file in initialMetadata.Values)
            {
                Assert.True(ManifestTransitions.SameIdentity(file, retired[file.Name]));
            }
            await AssertReadableAsync(successor, "successor-adoption");
        }

        await using var reopened = await OpenProviderAsync(finalCache.Path, client);
        await AssertReadableAsync(reopened, "successor-adoption");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFenceOpenSnapshotAfterSuccessorCompactsAndCollectsCloudSsts(bool coldReader)
    {
        using var firstCache = new TemporaryDirectory();
        using var coldReaderCache = new TemporaryDirectory();
        using var successorCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(firstCache.Path, CreateAzureLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30));
        var dependencies = new RuntimeDependencies(
            cloudHttpClient: client,
            leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            runtimeTimeProvider: monotonic,
            leaseClock: wallClock);
        if (coldReader)
        {
            await using (var seed = await PantsDatabase.OpenForTestingAsync(options, dependencies))
            {
                await CommitAndFlushAsync(seed, "snapshot-takeover/key");
            }

            wallClock.UtcNow += TimeSpan.FromSeconds(31);
            options = PantsOpenOptions.Cloud(coldReaderCache.Path, CreateAzureLocation())
                .WithBackgroundCompaction(false)
                .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
                .WithLeaseTimeToLive(TimeSpan.FromSeconds(30));
        }

        await using var original = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        if (!coldReader)
        {
            await CommitAndFlushAsync(original, "snapshot-takeover/key");
        }
        else
        {
            Assert.Empty(Directory.GetFiles(Path.Combine(coldReaderCache.Path, "sst"), "*.sst"));
        }

        var originalSst = Assert.Single(handler.GetObjectPaths("/sst/"));
        await using var snapshot = await original.Transactions.BeginAsync(
            original.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        handler.BlockLeaseWrites(ignoreCancellation: true);
        try
        {
            monotonic.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            // Exceed the old-validity-minus-margin renewal window while its proposed
            // lease is still live, so exact late-candidate expiry must run before takeover.
            monotonic.Advance(TimeSpan.FromSeconds(20));
            Assert.False(original.PersistentStorage!.IsPrimaryLeaseHealthy);
            wallClock.UtcNow += TimeSpan.FromSeconds(20);
        }
        finally
        {
            handler.ReleaseLeaseWrites();
        }

        // The released predecessor renewal must be expired. Wait for its exact late-release
        // write before acquiring the successor so the test does not create an unrelated CAS race.
        var expiredAt = wallClock.UtcNow - TimeSpan.FromMilliseconds(100) - TimeSpan.FromTicks(1);
        var expiredField = $"expires: {expiredAt.UtcDateTime:O}\n";
        await TestWait.UntilAsync(() => handler.GetObjectText("midge_primary_lease.json")
            .Contains(expiredField, StringComparison.Ordinal), "the predecessor's late renewal is expired");

        var successorOptions = PantsOpenOptions.Cloud(successorCache.Path, CreateAzureLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30));
        await using var successor = await PantsDatabase.OpenForTestingAsync(
            successorOptions,
            new RuntimeDependencies(
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        Assert.True(successor.PersistentStorage!.IsPrimaryLeaseHealthy);
        await using (var rewrite = await successor.Transactions.BeginAsync(
                         successor.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rewrite.Put("snapshot-takeover/key"u8.ToArray(), "new"u8.ToArray());
            await rewrite.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await successor.Maintenance.FlushAsync(successor.ColumnFamilies.DefaultFamily);
        await successor.Maintenance.CompactAllAsync();

        var readsBeforeSnapshotLookup = handler.SstRangeReads;
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            snapshot.GetAsync("snapshot-takeover/key"u8.ToArray()).AsTask());
        Assert.Equal(readsBeforeSnapshotLookup, handler.SstRangeReads);

        // The successor cannot prove that predecessor readers have a local copy.
        Assert.Contains(originalSst, handler.GetObjectPaths("/sst/"), StringComparer.Ordinal);
        await using var current = await successor.Transactions.BeginAsync(
            successor.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "new",
            TestBytes.ToText((await current.GetAsync("snapshot-takeover/key"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldCollectOrphanedCloudObjectsAfterCompaction()
    {
        await VerifyProviderCollectionAsync();
        await VerifySimulatedCollectionAsync();
    }

    [Fact]
    public async Task ShouldNotCollectCloudObjectsReferencedByManifest()
    {
        await VerifyProviderReferenceRetentionAsync();
        await VerifySimulatedReferenceRetentionAsync();
    }

    [Fact]
    public async Task ShouldHandleGcWhenCloudListFails()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "list-failure");
        var obsoletePaths = handler.GetObjectPaths("/sst/");
        Assert.Equal(2, obsoletePaths.Length);
        handler.FailSstList = true;

        await database.Maintenance.CompactAllAsync();

        Assert.All(obsoletePaths, path => Assert.Contains(
            path,
            handler.GetObjectPaths("/sst/"),
            StringComparer.Ordinal));
        Assert.Equal(3, handler.GetObjectPaths("/sst/").Length);
        Assert.Equal(0, handler.SstDeleteAttempts);
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        await AssertReadableAsync(database, "list-failure");
        handler.FailSstList = false;
    }

    [Fact]
    public async Task ShouldHandleGcWhenCloudDeleteFails()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "delete-failure");
        var obsoletePaths = handler.GetObjectPaths("/sst/");
        Assert.Equal(2, obsoletePaths.Length);
        handler.FailSstDeletes = true;

        await database.Maintenance.CompactAllAsync();

        Assert.All(obsoletePaths, path => Assert.Contains(
            path,
            handler.GetObjectPaths("/sst/"),
            StringComparer.Ordinal));
        Assert.Equal(3, handler.GetObjectPaths("/sst/").Length);
        Assert.True(handler.SstDeleteAttempts > 0);
        Assert.Equal(0, handler.UnconditionalSstDeleteAttempts);
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        await AssertReadableAsync(database, "delete-failure");
        handler.FailSstDeletes = false;
    }

    [Fact]
    public async Task ShouldCollectValidGarbageDespiteMalformedAndVersionlessObjects()
    {
        const string first = "sst/000000_01_00000000000000000001.sst";
        const string versionless = "sst/000000_01_00000000000000000002.sst";
        var store = new AnomalousCloudObjectStore(versionless, "sst/not-an-sst.txt");
        store.Seed(first);
        store.Seed(versionless);
        var collector = CreateCollector(store, static () => { });

        var complete = await collector.CollectAsync(CancellationToken.None);

        Assert.False(complete);
        Assert.False(store.Contains(first));
        Assert.True(store.Contains(versionless));
    }

    [Fact]
    public async Task ShouldStopDeletingImmediatelyWhenAuthorityIsLostMidPass()
    {
        const string first = "sst/000000_01_00000000000000000001.sst";
        const string second = "sst/000000_01_00000000000000000002.sst";
        var store = new AnomalousCloudObjectStore(null);
        store.Seed(first);
        store.Seed(second);
        var authorityChecks = 0;
        var collector = CreateCollector(store, () =>
        {
            if (Interlocked.Increment(ref authorityChecks) == 4)
            {
                throw new PantsFencedException("authority lost");
            }
        });

        var complete = await collector.CollectAsync(CancellationToken.None);

        Assert.False(complete);
        Assert.False(store.Contains(first));
        Assert.True(store.Contains(second));
    }

    [Fact]
    public async Task ShouldRejectRemoteSstDeletionAfterSuccessorTakesLeaseAtDeleteBoundary()
    {
        const string name = "000000_01_00000000000000000001.sst";
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        var sstStore = new TestCloudObjectStore();
        sstStore.Seed(objectKey, "obsolete"u8.ToArray());
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "predecessor",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var failpoints = new CloudCompactionFailpointHandler();
        failpoints.ArmCallback(
            Failpoint.BeforeCloudSstGarbageCollectionDelete,
            () => leaseStore.Seed(new CloudLeaseRecord(
                "successor",
                epoch + 1,
                "successor-token",
                clock.UtcNow,
                clock.UtcNow + TimeSpan.FromSeconds(10))));
        var collector = new CloudSstGarbageCollector(
            sstStore,
            _ => ValueTask.FromResult(new CloudSstRetentionProof(
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<uint, ulong> { [0] = 2 },
                [])),
            () => new HashSet<string>(StringComparer.Ordinal),
            lease.EnsureValid,
            lease.ValidateRemoteAsync,
            failpoints);

        var complete = await collector.CollectAsync(CancellationToken.None);

        Assert.False(complete);
        Assert.NotNull(await sstStore.HeadAsync(objectKey, CancellationToken.None));
        Assert.False(lease.IsHealthy);
    }

    [Fact]
    public async Task ShouldRecoverQuarantineLeftByCrashedSimulatedCollection()
    {
        using var directory = new TemporaryDirectory();
        var localRoot = Path.Combine(directory.Path, "local");
        var cloudRoot = Path.Combine(directory.Path, "cloud");
        var sstDirectory = Path.Combine(cloudRoot, "sst");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(sstDirectory);
        await CloudTestMetadata.SeedAsync(new SimulatedCloudObjectStore(cloudRoot), ManifestState.CreateInitial());
        var quarantined = Path.Combine(
            sstDirectory,
            "000000_01_00000000000000000001.sst.gc-crashed");
        File.WriteAllBytes(quarantined, "first"u8.ToArray());
        File.WriteAllBytes(
            Path.Combine(sstDirectory, "000000_01_00000000000000000002.sst"),
            "second"u8.ToArray());

        var complete = SimulatedCloudSstGarbageCollector.Collect(
            localRoot,
            cloudRoot,
            NullPantsFailpointHandler.Instance);

        Assert.True(complete);
        Assert.Empty(Directory.EnumerateFiles(sstDirectory));
    }

    [Fact]
    public async Task ShouldRetainSimulatedDeletionObligationsUntilRetryConfirmsRemoval()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new PersistentSstDeletionFailure();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "deletion-retry/").WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        await PrepareTwoSstsAsync(database, "simulated-deletion-retry");
        var original = CloudManifestReader.ReadManifest(directory.Path)!.Files.Select(static file => file.Clone()).ToArray();
        failpoints.Enabled = true;
        await database.Maintenance.CompactAllAsync();
        var pending = CloudManifestReader.ReadManifest(directory.Path)!.RetiredSsts;
        Assert.Equal(2, pending.Count);
        foreach (var file in original)
        {
            Assert.True(ManifestTransitions.SameIdentity(file, pending[file.Name]));
            Assert.True(File.Exists(Path.Combine(directory.Path, "cloud_store", "sst", file.Name)));
        }

        failpoints.Enabled = false;
        await database.Maintenance.CompactAllAsync();
        Assert.Single(GetSimulatedCloudSstPaths(directory.Path));
        Assert.Empty(CloudManifestReader.ReadManifest(directory.Path)!.RetiredSsts);
        var selected = await CloudMetadataAuthority.ReadCommittedFilesAsync(new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")), CancellationToken.None);
        Assert.Empty(CloudManifestReader.DecodeMetadata(selected!).RetiredSsts);
        await AssertReadableAsync(database, "simulated-deletion-retry");
    }

    static CloudSstGarbageCollector CreateCollector(
        ICloudObjectStore store,
        Action ensureAuthority) => new(
        store,
        _ => ValueTask.FromResult(new CloudSstRetentionProof(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<uint, ulong> { [0] = 100 },
            [])),
        () => new HashSet<string>(StringComparer.Ordinal),
        ensureAuthority,
        static _ => ValueTask.CompletedTask,
        NullPantsFailpointHandler.Instance);

    static async Task VerifyProviderCollectionAsync()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await PrepareTwoSstsAsync(database, "provider-collection");

        await database.Maintenance.CompactAllAsync();

        Assert.Single(handler.GetObjectPaths("/sst/"));
        Assert.Equal(2, handler.SstDeleteAttempts);
        Assert.Equal(0, handler.UnconditionalSstDeleteAttempts);
        await AssertReadableAsync(database, "provider-collection");
    }

    static async Task VerifySimulatedCollectionAsync()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "database/")
                .WithBackgroundCompaction(false));
        await PrepareTwoSstsAsync(database, "simulated-collection");

        await database.Maintenance.CompactAllAsync();

        Assert.Single(GetSimulatedCloudSstPaths(directory.Path));
        Assert.Empty(CloudManifestReader.ReadManifest(directory.Path)!.RetiredSsts);
        var selected = await CloudMetadataAuthority.ReadCommittedFilesAsync(new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")), CancellationToken.None);
        Assert.Empty(CloudManifestReader.DecodeMetadata(selected!).RetiredSsts);
        await AssertReadableAsync(database, "simulated-collection");
    }

    static async Task VerifyProviderReferenceRetentionAsync()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenProviderAsync(cache.Path, client);
        await CommitAndFlushAsync(database, "provider-reference/key");
        var referencedPath = Assert.Single(handler.GetObjectPaths("/sst/"));

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Equal(referencedPath, Assert.Single(handler.GetObjectPaths("/sst/")));
        await AssertValueAsync(database, "provider-reference/key");
    }

    static async Task VerifySimulatedReferenceRetentionAsync()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "database/")
                .WithBackgroundCompaction(false));
        await CommitAndFlushAsync(database, "simulated-reference/key");
        var referencedPath = Assert.Single(GetSimulatedCloudSstPaths(directory.Path));

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Equal(referencedPath, Assert.Single(GetSimulatedCloudSstPaths(directory.Path)));
        await AssertValueAsync(database, "simulated-reference/key");
    }

    static ValueTask<IPantsDatabase> OpenProviderAsync(string path, HttpClient client) =>
        OpenProviderAsync(path, client, NullPantsFailpointHandler.Instance);

    static ValueTask<IPantsDatabase> OpenProviderAsync(
        string path,
        HttpClient client,
        IFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(path, CreateAzureLocation())
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(
                failpoints,
                cloudHttpClient: client));

    static PantsCloudStorageLocation CreateAzureLocation() =>
        new(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");

    static async Task PrepareTwoSstsAsync(IPantsDatabase database, string prefix)
    {
        await CommitAndFlushAsync(database, $"{prefix}/first", forceRewrite: true);
        await CommitAndFlushAsync(database, $"{prefix}/second", forceRewrite: true);
        Assert.Equal(2, (await database.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
    }

    static async Task CommitAndFlushAsync(IPantsDatabase database, string key, bool forceRewrite = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(Encoding.UTF8.GetBytes(key), "value"u8.ToArray());
        if (forceRewrite)
        {
            // Overlap only empty keyspace so this fixture requires a physical rewrite.
            transaction.DeleteRange("!overlap/a"u8.ToArray(), "!overlap/z"u8.ToArray());
        }
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task AssertReadableAsync(IPantsDatabase database, string prefix)
    {
        await AssertValueAsync(database, $"{prefix}/first");
        await AssertValueAsync(database, $"{prefix}/second");
    }

    static async Task AssertValueAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(Encoding.UTF8.GetBytes(key));
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
    }

    static string[] GetSimulatedCloudSstPaths(string root) =>
        Directory.GetFiles(
            Path.Combine(root, "cloud_store", "sst"),
            "*.sst",
            SearchOption.TopDirectoryOnly);

    sealed class PersistentSstDeletionFailure : IFailpointHandler
    {
        public bool Enabled { get; set; }

        public void Hit(Failpoint failpoint)
        {
            if (Enabled && failpoint == Failpoint.BeforeCloudSstGarbageCollectionDelete)
            {
                throw new IOException("Injected simulated SST deletion failure.");
            }
        }
    }

    sealed class AnomalousCloudObjectStore(
        string? versionlessKey,
        params string[] extraListedKeys) : ICloudObjectStore
    {
        readonly SnapshotConsistencyCloudObjectStore _inner = new();

        public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken) =>
            _inner.GetAsync(objectKey, cancellationToken);

        public async ValueTask<CloudObjectMetadata?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken)
        {
            var metadata = await _inner.HeadAsync(objectKey, cancellationToken);
            return metadata is not null && StringComparer.Ordinal.Equals(objectKey, versionlessKey)
                ? metadata with { ETag = " ", Generation = null }
                : metadata;
        }

        public ValueTask<bool> PutAsync(string objectKey, ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition, CancellationToken cancellationToken) =>
            _inner.PutAsync(objectKey, data, condition, cancellationToken);

        public async ValueTask<CloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken)
        {
            var page = await _inner.ListPageAsync(prefix, continuationToken, cancellationToken);
            return continuationToken is null
                ? new CloudObjectListPage([.. page.ObjectKeys, .. extraListedKeys], null)
                : page;
        }

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(string objectKey,
            CloudObjectDeleteCondition condition, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(objectKey, condition, cancellationToken);

        public void Seed(string key) => _inner.Seed(key, "data"u8);

        public bool Contains(string key) => _inner.GetData(key) is not null;
    }
}
