using System.Net;
using System.Text.Json;
using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudBootstrapTests
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task ShouldRejectASimulatedOpenOfAnAlreadyOwnedCacheBeforeSharedMutation()
    {
        using var cache = new TemporaryDirectory();
        await using var local = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(cache.Path));
        await using (var write = await local.Transactions.BeginAsync(local.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            write.Put("row"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }
        var before = LocalAuthorityFiles(cache.Path).ToDictionary(static path => path,
            path => File.ReadAllBytes(Path.Combine(cache.Path, path)), StringComparer.Ordinal);

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(cache.Path, "pants-tests", "bootstrap/")).AsTask());

        Assert.False(Directory.Exists(Path.Combine(cache.Path, "cloud_store")));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), LocalAuthorityFiles(cache.Path));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(cache.Path, name)));
        }
        await AssertValueAsync(local);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRefuseAReplacedCacheDirectoryAfterTheBootstrapUploadCompletes(bool retargetAlias)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TemporaryDirectory();
        var cachePath = Path.Combine(directory.Path, "cache");
        if (retargetAlias)
        {
            var originalPath = Path.Combine(directory.Path, "original");
            Directory.CreateDirectory(originalPath);
            Directory.CreateSymbolicLink(cachePath, originalPath);
        }
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cachePath, backend) { HoldFirstGenerationUpload = true };
        using var client = new HttpClient(handler);
        var opening = PantsDatabase.OpenForTestingAsync(CreateOptions(cachePath),
            new RuntimeDependencies(cloudHttpClient: client)).AsTask();
        IPantsDatabase? original = null;
        IPantsDatabase? replacement = null;
        try
        {
            await handler.WaitForGenerationUploadAsync();
            if (retargetAlias)
            {
                var replacementPath = Path.Combine(directory.Path, "replacement");
                Directory.CreateDirectory(replacementPath);
                Directory.Delete(cachePath);
                Directory.CreateSymbolicLink(cachePath, replacementPath);
            }
            else
            {
                Directory.Move(cachePath, Path.Combine(directory.Path, "renamed"));
                Directory.CreateDirectory(cachePath);
            }
            replacement = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(cachePath));
            await using (var write = await replacement.Transactions.BeginAsync(replacement.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                write.Put("row"u8.ToArray(), "value"u8.ToArray());
                await write.CommitAsync(PantsWriteOptions.Sync);
            }
            var before = LocalAuthorityFiles(cachePath).ToDictionary(static path => path,
                path => File.ReadAllBytes(Path.Combine(cachePath, path)), StringComparer.Ordinal);
            handler.ReleaseGenerationUpload();
            var error = await Record.ExceptionAsync(async () => original = await opening.WaitAsync(TestTimeouts.Expected));
            Assert.IsType<PantsInvalidPathException>(error);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), LocalAuthorityFiles(cachePath));
            foreach (var (name, bytes) in before)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(cachePath, name)));
            }
            await AssertValueAsync(replacement);
        }
        finally
        {
            handler.ReleaseGenerationUpload();
            if (original is not null)
            {
                await original.DisposeAsync();
            }
            if (replacement is not null)
            {
                await replacement.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldExcludeAnotherCacheWriterWhileFirstGenerationUploadIsHeld(bool providerContender)
    {
        using var cache = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cache.Path, backend) { HoldFirstGenerationUpload = true };
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var opening = PantsDatabase.OpenForTestingAsync(CreateOptions(cache.Path), dependencies).AsTask();
        IPantsDatabase? contender = null;
        try
        {
            await handler.WaitForGenerationUploadAsync();
            var attempted = providerContender
                ? PantsOpenOptions.Cloud(cache.Path, CreateLocation() with { Prefix = "other-prefix/" })
                : PantsOpenOptions.Local(cache.Path);
            var error = await Record.ExceptionAsync(async () =>
                contender = await PantsDatabase.OpenForTestingAsync(attempted.WithBackgroundCompaction(false), dependencies));
            Assert.IsType<PantsLeaseHeldException>(error);
            Assert.Empty(LocalAuthorityFiles(cache.Path));
        }
        finally
        {
            if (contender is not null)
            {
                await contender.DisposeAsync();
            }
            handler.ReleaseGenerationUpload();
            try
            {
                await using var original = await opening.WaitAsync(TestTimeouts.Expected);
            }
            catch (PantsException) when (contender is not null)
            {
                // The red fixture already reports admitted conflicting ownership; its unrelated
                // startup failure must not replace that assertion during bounded cleanup.
            }
        }
    }

    [Fact]
    public async Task ShouldRejectAnAlreadyOwnedCacheBeforeAnyRemoteMutation()
    {
        using var cache = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(backend);
        await using var local = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(cache.Path));
        await using (var write = await local.Transactions.BeginAsync(local.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            write.Put("row"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }
        var before = LocalAuthorityFiles(cache.Path).ToDictionary(static path => path,
            path => File.ReadAllBytes(Path.Combine(cache.Path, path)), StringComparer.Ordinal);

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenForTestingAsync(CreateOptions(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client)).AsTask());

        Assert.Empty(backend.GetObjectPaths("/"));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), LocalAuthorityFiles(cache.Path));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(cache.Path, name)));
        }
        await AssertValueAsync(local);
    }

    [Fact]
    public async Task ShouldUseBoundedCatalogConfirmationBeforeTheFirstGenerationUpload()
    {
        using var cache = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cache.Path, backend);
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(CreateOptions(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        Assert.True(handler.GenerationUploads > 0);
        Assert.Equal(0, handler.CatalogFullReadsBeforeFirstGeneration);
        await AssertMissingAsync(database, "row");
    }

    [Fact]
    public void ShouldAdmitAndRetainBootstrapCandidateMemoryUntilDisposal()
    {
        var rejected = new ResourceBudget(1024);
        MetadataAccountingHandle? failedAccounting = null;
        Assert.Throws<PantsResourceLimitException>(() =>
            CloudControlMetadataSnapshot.CreateBootstrapCandidate(rejected, out failedAccounting));
        Assert.Equal(0, rejected.Current);
        var failed = failedAccounting!.Snapshot();
        var failedBucket = failed.Bucket(MetadataOrigin.Bootstrap, MetadataMedium.MemoryOnly);
        Assert.Equal(1UL, failedBucket[MetadataCounter.OperationFailures]);
        Assert.Equal(0UL, failedBucket[MetadataCounter.SnapshotIssuedBytes]);
        Assert.Equal(0UL, failedBucket[MetadataCounter.SnapshotDurableBytes]);
        Assert.Equal(0UL, failedBucket[MetadataCounter.CheckpointCompleteCount]);
        Assert.All(failed.Buckets, static bucket => Assert.Equal(0UL, bucket.ActiveOperations));
        Assert.Equal(0UL, failed.LateMutations);
        Assert.Equal(0UL, failed.IncompleteObservations);
        Assert.False(failed.Overflow);
        var admitted = new ResourceBudget(1024 * 1024);
        using (var candidate = CloudControlMetadataSnapshot.CreateBootstrapCandidate(admitted))
        {
            Assert.True(admitted.Current > 0);
            var manifest = ManifestCodec.Decode(candidate.Files["manifest.snapshot.json"].Span);
            Assert.Empty(manifest.Files);
            Assert.Empty(manifest.ColumnFamilies);
            Assert.Equal(0UL, manifest.FlushedThrough[0]);
            Assert.True(LocalDiskStore.IsSupportedFormatMarker(candidate.Files["FORMAT"].Span));
            var snapshot = candidate.BootstrapAccounting!.Snapshot();
            var bucket = snapshot.Bucket(MetadataOrigin.Bootstrap, MetadataMedium.MemoryOnly);
            var expected = checked((ulong)candidate.Files["manifest.snapshot.json"].Length);
            Assert.Equal(expected, bucket[MetadataCounter.SnapshotIssuedBytes]);
            Assert.Equal(expected, bucket[MetadataCounter.SnapshotReturnedBytes]);
            Assert.Equal(expected, bucket[MetadataCounter.SnapshotDurableBytes]);
            Assert.Equal(1UL, bucket[MetadataCounter.CheckpointCompleteCount]);
            Assert.Equal(0UL, bucket[MetadataCounter.OperationFailures]);
            Assert.All(snapshot.Buckets.Where(static item => item.Medium == MetadataMedium.Persistent),
                static item => Assert.All(item.Counters, static counter => Assert.Equal(0UL, counter)));
            Assert.All(snapshot.Buckets, static item => Assert.Equal(0UL, item.ActiveOperations));
        }
        Assert.Equal(0, admitted.Current);
    }

    [Fact]
    public async Task ShouldRetainBootstrapOwnershipUntilHeldUploadStopsAfterOpenTimeout()
    {
        using var cache = new TemporaryDirectory();
        using var contender = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cache.Path, backend) { HoldFirstGenerationUpload = true };
        using var client = new HttpClient(handler);
        var time = new ManualTimeProvider();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time,
            startupObserver: observed =>
            {
                if (observed == StartupEvent.CleanupFinished)
                {
                    cleanup.TrySetResult();
                }
            });
        var opening = PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path).WithOpenTimeout(TimeSpan.FromSeconds(1)), dependencies).AsTask();
        try
        {
            await handler.WaitForGenerationUploadAsync();
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.False(cleanup.Task.IsCompleted);
            Assert.Empty(LocalAuthorityFiles(cache.Path));
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
                PantsDatabase.OpenAsync(PantsOpenOptions.Local(cache.Path)).AsTask());
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenForTestingAsync(
                CreateOptions(contender.Path), new RuntimeDependencies(cloudHttpClient: client)).AsTask());
        }
        finally
        {
            handler.ReleaseGenerationUpload();
            await cleanup.Task.WaitAsync(TestTimeouts.Expected);
        }

        Assert.Empty(LocalAuthorityFiles(cache.Path));
        Assert.Equal(0UL, CloudMetadataAuthority.ReadGenerationOrdinal(backend.GetObjectBytes("/metadata/committed-pointer.v1")));
        await using var retry = await PantsDatabase.OpenForTestingAsync(CreateOptions(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        await AssertMissingAsync(retry, "row");
        await PutAsync(retry);
    }

    [Fact]
    public async Task ShouldLeaveNoLocalAuthorityAndRetrySameCacheAfterFirstGenerationUploadFails()
    {
        using var cache = new TemporaryDirectory();
        using var fresh = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cache.Path, backend) { FailGenerationUploads = true };
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = CreateOptions(cache.Path);

        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask());

        Assert.True(handler.GenerationUploads > 0);
        Assert.Empty(handler.LocalAuthorityAtFirstUpload!);
        Assert.Empty(LocalAuthorityFiles(cache.Path));
        Assert.Equal(0UL, CloudMetadataAuthority.ReadGenerationOrdinal(backend.GetObjectBytes("/metadata/committed-pointer.v1")));
        handler.FailGenerationUploads = false;
        await using (var retry = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await AssertMissingAsync(retry, "row");
            await PutAsync(retry);
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(CreateOptions(fresh.Path), dependencies);
        await AssertValueAsync(recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPublishFirstGenerationBeforeLocalFilesWhenPointerCasResponseIsLost(bool freshCache)
    {
        using var cache = new TemporaryDirectory();
        using var fresh = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var handler = new BootstrapHandler(cache.Path, backend) { LoseFirstCommittedPointerResponse = true };
        using var client = new HttpClient(handler);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(CreateOptions(cache.Path), dependencies))
        {
            Assert.True(handler.CommittedPointerResponseLost);
            Assert.Empty(handler.LocalAuthorityAtFirstUpload!);
            Assert.True(CloudMetadataAuthority.ReadGenerationOrdinal(backend.GetObjectBytes("/metadata/committed-pointer.v1")) > 0);
            Assert.Equal(0UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await AssertMissingAsync(database, "row");
            await PutAsync(database);
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(freshCache ? fresh.Path : cache.Path), dependencies);
        await AssertValueAsync(recovered);
    }

    [Fact]
    public async Task ShouldRefuseForeignLocalMetadataBeforeBootstrappingAnUncommittedProvider()
    {
        using var cache = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(backend);
        var format = "midge-format-version=9\n"u8.ToArray();
        var manifest = ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, "FORMAT"), format);
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, "manifest.snapshot.json"), manifest);

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path), new RuntimeDependencies(cloudHttpClient: client)).AsTask());

        Assert.Equal(format, await File.ReadAllBytesAsync(Path.Combine(cache.Path, "FORMAT")));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(cache.Path, "manifest.snapshot.json")));
        Assert.Empty(backend.GetObjectPaths("/metadata/generations/"));
    }

    [Theory]
    [InlineData("sst")]
    [InlineData("ddl")]
    [InlineData("catalog")]
    public async Task ShouldRefuseForeignRemoteHistoryBeforePublishingAnEmptyGeneration(string history)
    {
        using var cache = new TemporaryDirectory();
        using var backend = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(backend);
        var store = await CloudObjectStoreFactory.CreateAsync(CreateLocation(), TestTimeouts.Expected, client);
        string key;
        byte[] bytes;
        switch (history)
        {
            case "sst":
                key = PantsCloudObjectLayout.SstPrefix + "000000_00_00000000000000000001.sst";
                bytes = "retained foreign SST bytes"u8.ToArray();
                break;
            case "ddl":
                key = PantsCloudObjectLayout.DdlRegistryObjectKey;
                bytes = CloudDdlJson.SerializeRegistry(new CloudDdlRegistry
                {
                    Epoch = 1,
                    ColumnFamilies = [new CloudDdlColumnFamily { Id = 7, Name = "foreign", CreatedAt = 1 }]
                });
                break;
            default:
                key = PantsCloudObjectLayout.WalCatalogObjectKey;
                bytes = JsonSerializer.SerializeToUtf8Bytes(new ProviderWalCatalog
                {
                    FencingEpoch = 1,
                    NextSegmentId = 1,
                    SequenceFloor = 5
                }, JsonOptions);
                Assert.True(await store.PutAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                    bytes, new PantsCloudObjectWriteCondition.IfAbsent()));
                break;
        }

        Assert.True(await store.PutAsync(key, bytes, new PantsCloudObjectWriteCondition.IfAbsent()));

        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path), new RuntimeDependencies(cloudHttpClient: client)).AsTask());

        Assert.Equal(bytes, (await store.GetAsync(key))!.Data.ToArray());
        Assert.Empty(backend.GetObjectPaths("/metadata/generations/"));
        Assert.Empty(LocalAuthorityFiles(cache.Path));
    }

    static PantsOpenOptions CreateOptions(string path) => PantsOpenOptions.Cloud(path, CreateLocation())
        .WithBackgroundCompaction(false);

    static PantsCloudStorageLocation CreateLocation() => new(new PantsAzureBlobProvider(
        "account", "container", new Uri("https://storage.example.test"),
        new PantsAzureCredentialSource.SasToken("sig=test")), "bootstrap/");

    static string[] LocalAuthorityFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path))
        .Where(static path => path is "FORMAT" or "manifest.snapshot.json" or "manifest.journal" or "intent_log.json" ||
                              path.StartsWith("wal/", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    static async Task PutAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("row"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task AssertMissingAsync(IPantsDatabase database, string key)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString(key)));
    }

    static async Task AssertValueAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    sealed class BootstrapHandler(string root, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        readonly TaskCompletionSource _generationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _generationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _generationUploads;
        int _lostResponse;
        int _catalogFullReadsBeforeFirstGeneration;

        public bool FailGenerationUploads { get; set; }
        public bool HoldFirstGenerationUpload { get; init; }
        public bool LoseFirstCommittedPointerResponse { get; init; }
        public int GenerationUploads => Volatile.Read(ref _generationUploads);
        public int CatalogFullReadsBeforeFirstGeneration => Volatile.Read(ref _catalogFullReadsBeforeFirstGeneration);
        public bool CommittedPointerResponseLost => Volatile.Read(ref _lostResponse) != 0;
        public string[]? LocalAuthorityAtFirstUpload { get; private set; }

        public Task WaitForGenerationUploadAsync() => _generationEntered.Task.WaitAsync(TestTimeouts.Expected);

        public void ReleaseGenerationUpload() => _generationRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/" + PantsCloudObjectLayout.WalCatalogObjectKey, StringComparison.Ordinal) &&
                request.Headers.Range is null && Volatile.Read(ref _generationUploads) == 0)
            {
                Interlocked.Increment(ref _catalogFullReadsBeforeFirstGeneration);
            }
            if (request.Method == HttpMethod.Put && path.Contains("/metadata/generations/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _generationUploads) == 1)
                {
                    LocalAuthorityAtFirstUpload = LocalAuthorityFiles(root);
                    _generationEntered.TrySetResult();
                    if (HoldFirstGenerationUpload)
                    {
                        await _generationRelease.Task.WaitAsync(TestTimeouts.Expected, CancellationToken.None);
                        // A submitted effect can outlive cancellation. The preparation owner must
                        // retain the lease and candidate through this actual late completion.
                        return await base.SendAsync(request, CancellationToken.None);
                    }
                }

                if (FailGenerationUploads)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            if (LoseFirstCommittedPointerResponse && request.Method == HttpMethod.Put &&
                path.EndsWith("/metadata/committed-pointer.v1", StringComparison.Ordinal) &&
                CloudMetadataAuthority.ReadGenerationOrdinal(await request.Content!.ReadAsByteArrayAsync(cancellationToken)) > 0 &&
                Interlocked.CompareExchange(ref _lostResponse, 1, 0) == 0)
            {
                using var applied = await base.SendAsync(request, cancellationToken);
                Assert.True(applied.IsSuccessStatusCode);
                throw new HttpRequestException("Lost the applied first-generation pointer response.");
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
