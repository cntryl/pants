using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class ProviderCloudPersistenceTests
{
    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldPublishOneCatalogTransitionGivenEpochCompatibleWalBatch()
    {
        using var cache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var walStore = new CountingCloudObjectStore();
        var persistence = new ProviderCloudPersistence(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            lease);
        var segments = Enumerable.Range(1, 3)
            .Select(index => new SealedWalSegment(
                checked((ulong)index),
                epoch,
                checked((ulong)index),
                $"{index}.wal",
                [(byte)index]))
            .ToArray();

        await persistence.PublishWalBatchAsync(segments, CancellationToken.None);

        Assert.Equal(4, walStore.PutCount);
        Assert.Equal(5, walStore.GetCount);
        Assert.True(walStore.PayloadBytesCopied > 3);
        var hydrated = await ProviderCloudPersistence.HydrateLocalCacheAsync(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);
        Assert.Equal([1UL, 2UL, 3UL], hydrated.PublishedWalSegments.Keys);
    }

    [Fact]
    public async Task ShouldRejectWalBatchGivenWriterEpochChangesWithinBatch()
    {
        using var cache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var walStore = new CountingCloudObjectStore();
        var persistence = new ProviderCloudPersistence(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            lease);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => persistence.PublishWalBatchAsync(
            [
                new SealedWalSegment(1, epoch, 1, "1.wal", [1]),
                new SealedWalSegment(2, epoch + 1, 2, "2.wal", [2])
            ],
            CancellationToken.None).AsTask());

        Assert.Equal(0, walStore.PutCount);
    }

    [Fact]
    public async Task ShouldHydrateSegmentGivenCatalogEntryHasSegmentIdZero()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new SnapshotConsistencyCloudObjectStore();
        var sstStore = new SnapshotConsistencyCloudObjectStore();
        var controlStore = new SnapshotConsistencyCloudObjectStore();
        var segmentBytes = "wal-segment-zero"u8.ToArray();
        var objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(1, 0);
        walStore.Seed(objectKey, segmentBytes);
        var catalog = new ProviderWalCatalog
        {
            FencingEpoch = 1,
            Segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>
            {
                [0] = new ProviderPublishedWalSegment
                {
                    SegmentId = 0,
                    WriterEpoch = 1,
                    MaximumSequence = 1,
                    SizeBytes = checked((ulong)segmentBytes.Length),
                    ContentCrc32C = DiskFormat.Crc32C(segmentBytes),
                    ObjectKey = objectKey
                }
            }
        };
        walStore.Seed(
            PantsCloudObjectLayout.WalCatalogObjectKey,
            JsonSerializer.SerializeToUtf8Bytes(catalog, CatalogJsonOptions));

        var result = await ProviderCloudPersistence.HydrateLocalCacheAsync(
            cache.Path,
            walStore,
            sstStore,
            controlStore,
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);

        Assert.False(result.RequiresSalvage);
        Assert.True(result.PublishedWalSegments.ContainsKey(0));
        Assert.True(File.Exists(Path.Combine(
            cache.Path,
            "wal",
            "00000000000000000000.wal")));
    }

    [Fact]
    public async Task ShouldSkipControlPublicationGivenRemoteBytesAlreadyMatch()
    {
        using var cache = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(cache.Path, "FORMAT"), "format"u8.ToArray());
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await lease.AcquireAsync(CancellationToken.None);
        var controlStore = new TestCloudObjectStore();
        var persistence = new ProviderCloudPersistence(
            cache.Path,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            lease);
        await persistence.MirrorMetadataAndSstsAsync(CancellationToken.None);
        var baseline = controlStore.PutCount;

        await persistence.MirrorMetadataAndSstsAsync(CancellationToken.None);

        Assert.Equal(baseline, controlStore.PutCount);
    }

    [Fact]
    public async Task ShouldRejectStaleDdlCasGivenSuccessorClaimsRegistryDuringRequest()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var controlStore = new TestCloudObjectStore();
        var firstClock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var firstLease = new CloudLeaseCoordinator(
            leaseStore,
            firstClock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await firstLease.AcquireAsync(CancellationToken.None);
        var firstPersistence = new ProviderCloudPersistence(
            firstCache.Path,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            firstLease);
        Assert.True(await firstPersistence.CompareExchangeDdlRegistryAsync(
            new CloudDdlRegistry(),
            null,
            CancellationToken.None));
        var observed = Assert.IsType<CloudDdlRegistryObject>(
            await firstPersistence.ReadDdlRegistryAsync(CancellationToken.None));
        var putStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePut = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeNextPutAsync = async cancellationToken =>
        {
            putStarted.SetResult();
            await releasePut.Task.WaitAsync(cancellationToken);
        };
        var staleCas = firstPersistence.CompareExchangeDdlRegistryAsync(
                observed.Registry.Clone(),
                observed.Version,
                CancellationToken.None)
            .AsTask();
        await putStarted.Task;

        firstClock.UtcNow += TimeSpan.FromSeconds(11);
        var secondClock = new ManualClock(firstClock.UtcNow);
        using var secondLease = new CloudLeaseCoordinator(
            leaseStore,
            secondClock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await secondLease.AcquireAsync(CancellationToken.None);
        var secondPersistence = new ProviderCloudPersistence(
            secondCache.Path,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            secondLease);

        await secondPersistence.FenceDdlRegistryAsync(
            new CloudDdlRegistry(),
            CancellationToken.None);
        releasePut.SetResult();

        await Assert.ThrowsAsync<PantsFencedException>(() => staleCas);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldDiscardStaleDdlCasGivenSuccessorClaimAlreadyStarted(
        bool registryExists)
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var controlStore = new TestCloudObjectStore();
        var firstClock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var firstLease = new CloudLeaseCoordinator(
            leaseStore,
            firstClock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await firstLease.AcquireAsync(CancellationToken.None);
        var firstPersistence = new ProviderCloudPersistence(
            firstCache.Path,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            firstLease);
        CloudDdlRegistry staleRegistry;
        string? expectedVersion;
        if (registryExists)
        {
            Assert.True(await firstPersistence.CompareExchangeDdlRegistryAsync(
                new CloudDdlRegistry(),
                null,
                CancellationToken.None));
            var observed = Assert.IsType<CloudDdlRegistryObject>(
                await firstPersistence.ReadDdlRegistryAsync(CancellationToken.None));
            staleRegistry = observed.Registry.Clone();
            expectedVersion = observed.Version;
        }
        else
        {
            staleRegistry = new CloudDdlRegistry();
            expectedVersion = null;
        }

        staleRegistry.Epoch = 1;
        using var staleEdit = JsonDocument.Parse(
            """{"CreateColumnFamily":{"id":1,"name":"stale-column-family","created_at":1}}""");
        staleRegistry.Operations.Add(new CloudDdlOperation
        {
            OperationId = "stale-operation",
            Edit = staleEdit.RootElement.Clone()
        });
        var stalePutStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStalePut = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeNextPutAsync = async cancellationToken =>
        {
            stalePutStarted.SetResult();
            await releaseStalePut.Task.WaitAsync(cancellationToken);
        };
        var staleCas = firstPersistence.CompareExchangeDdlRegistryAsync(
                staleRegistry,
                expectedVersion,
                CancellationToken.None)
            .AsTask();
        await stalePutStarted.Task;

        firstClock.UtcNow += TimeSpan.FromSeconds(11);
        var secondClock = new ManualClock(firstClock.UtcNow);
        using var secondLease = new CloudLeaseCoordinator(
            leaseStore,
            secondClock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await secondLease.AcquireAsync(CancellationToken.None);
        var secondPersistence = new ProviderCloudPersistence(
            secondCache.Path,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            secondLease);
        var claimStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeNextPutAsync = async cancellationToken =>
        {
            claimStarted.SetResult();
            await releaseClaim.Task.WaitAsync(cancellationToken);
        };
        var claim = secondPersistence.FenceDdlRegistryAsync(
                new CloudDdlRegistry(),
                CancellationToken.None)
            .AsTask();
        await claimStarted.Task;

        releaseStalePut.SetResult();
        await Assert.ThrowsAsync<PantsFencedException>(() => staleCas);
        releaseClaim.SetResult();
        await claim;

        var claimed = Assert.IsType<CloudDdlRegistryObject>(
            await secondPersistence.ReadDdlRegistryAsync(CancellationToken.None));
        Assert.Empty(claimed.Registry.Operations);
        Assert.Equal(0UL, claimed.Registry.Epoch);
    }

    [Fact]
    public async Task ShouldFenceWalCatalogToNewLeaseBeforeAcceptingWrites()
    {
        using var firstCache = new TemporaryDirectory();
        using var secondCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);

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

        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        Assert.Equal(2UL, catalog.RootElement.GetProperty("fencing_epoch").GetUInt64());
    }

    [Fact]
    public async Task ShouldRejectOpenGivenWalCatalogFenceSuccessWithoutReadback()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler
        {
            AcknowledgeWalCatalogWritesWithoutPersisting = true
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation()),
            new RuntimeDependencies(cloudHttpClient: client)).AsTask());
    }

    [Fact]
    public async Task ShouldKeepCloudLeaseHealthyGivenTtlClockAdvances()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation())
            .WithTtlClock(clock);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        var initialSequence = (await database.GetRuntimeMetricsAsync()).CurrentSequence;
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        clock.UtcNow += TimeSpan.FromMinutes(1);

        await transaction.CommitAsync(PantsWriteOptions.BestEffort);

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.True(metrics.CurrentSequence > initialSequence);
        Assert.True(database.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldNotSealCloudAsyncWalGivenOnlyTtlClockAdvances()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var ttlClock = new ManualClock(DateTimeOffset.UnixEpoch);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation())
            .WithTtlClock(ttlClock)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                int.MaxValue));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        ttlClock.UtcNow += TimeSpan.FromHours(2);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.Equal(1, metrics.WalPendingWrites);
        Assert.Empty(handler.GetObjectPaths("/wal/epochs/"));
    }

    [Fact]
    public async Task ShouldFailCloudStrictCommitGivenWalUploadFailure()
    {
        using var cache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(cache.Path, location),
                         dependencies))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            handler.FailWalWrites = true;

            await Assert.ThrowsAsync<PantsInternalException>(() =>
                transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
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
    public async Task ShouldRejectCloudStrictAckGivenWalPutSuccessWithoutReadback()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation()),
            new RuntimeDependencies(cloudHttpClient: client));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        handler.AcknowledgeWalWritesWithoutPersisting = true;

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(cache.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));
        handler.AcknowledgeWalWritesWithoutPersisting = false;
    }

    [Fact]
    public async Task ShouldRejectCloudStrictAckGivenCatalogPutSuccessWithoutReadback()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation()),
            new RuntimeDependencies(cloudHttpClient: client));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        handler.AcknowledgeWalCatalogWritesWithoutPersisting = true;

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(cache.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));
        handler.AcknowledgeWalCatalogWritesWithoutPersisting = false;
    }

    [Fact]
    public async Task ShouldKeepCloudAsyncCommitVisibleGivenWalUploadFailure()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await handler.WaitForFailedWalWriteAsync(timeout.Token);

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("key"u8.ToArray()))));
        Assert.True(handler.FailedWalWriteAttempts > 0);
        var metrics = await WaitForPersistenceAnomalyAsync(database);
        Assert.True(metrics.WalCloudDurableSequence < metrics.CurrentSequence);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);

        handler.FailWalWrites = false;
    }

    [Fact]
    public async Task ShouldAdvanceCloudDurabilityOnlyAfterContiguousWalUploads()
    {
        using var cache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, location)
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1));

        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            handler.FailWalWrites = true;
            await CommitCloudAsyncAsync(database, "first"u8.ToArray());
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await handler.WaitForFailedWalWriteAsync(timeout.Token);
            }

            handler.FailWalWrites = false;
            await CommitCloudAsyncAsync(database, "second"u8.ToArray());
            await WaitForCloudDurabilityAsync(database);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(replacementCache.Path, location),
            dependencies);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("first"u8.ToArray()))));
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("second"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldResumeFailedCloudAsyncUploadFromRecoveredLocalWal()
    {
        using var cache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
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
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);

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
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
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

    [Fact]
    public async Task ShouldRecoverAcknowledgedCloudAsyncWriteGivenMetadataPublicationFailsAfterLocalFlush()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateAzureLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(cache.Path, location)
                             .WithBackgroundCompaction(false),
                         dependencies))
        {
            await CommitCloudAsyncAsync(database, "local-only-flush"u8.ToArray());
            handler.FailMetadataWrites = true;

            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.FlushAsync(database.DefaultColumnFamily).AsTask());
        }

        handler.FailMetadataWrites = false;
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, location).WithBackgroundCompaction(false),
            dependencies);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("local-only-flush"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRejectMetadataRegressionGivenRemoteManifestAheadOfLocalCache()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, CreateAzureLocation())
                .WithBackgroundCompaction(false),
            dependencies);
        await CommitCloudStrictAsync(database, "first"u8.ToArray());
        await database.FlushAsync(database.DefaultColumnFamily);
        var remoteManifest = JsonNode.Parse(
            handler.GetObjectText("/metadata/manifest.snapshot.json"))!.AsObject();
        remoteManifest["last_persisted_sequence"] = 1_000_000UL;
        handler.ReplaceObjectText(
            "/metadata/manifest.snapshot.json",
            remoteManifest.ToJsonString());

        await Assert.ThrowsAsync<PantsFencedException>(() => CommitCloudStrictAsync(database, "second"u8.ToArray()));

        using var readback = JsonDocument.Parse(
            handler.GetObjectText("/metadata/manifest.snapshot.json"));
        Assert.Equal(
            1_000_000UL,
            readback.RootElement.GetProperty("last_persisted_sequence").GetUInt64());
    }

    static PantsCloudStorageLocation CreateAzureLocation() =>
        new(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");

    static async Task CommitCloudAsyncAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }

    static async Task CommitCloudStrictAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task WaitForCloudDurabilityAsync(IPantsDatabase database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var metrics = await database.GetRuntimeMetricsAsync(timeout.Token);
            if (metrics.WalCloudDurableSequence >= metrics.CurrentSequence)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    static async Task<PantsRuntimeMetrics> WaitForPersistenceAnomalyAsync(
        IPantsDatabase database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var metrics = await database.GetRuntimeMetricsAsync(timeout.Token);
            if (metrics.Health == PantsEngineHealth.Degraded)
            {
                return metrics;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
