using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudMetadataAuthorityTests
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task ShouldRecoverExactCommittedPresenceSetAfterPointerClaimBySuccessor()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions));
        var store = new CountingCloudObjectStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseStore = new TestCloudLeaseStore();
        using var firstLease = CreateLease(leaseStore, clock, "first");
        await firstLease.AcquireAsync(CancellationToken.None);
        using var first = new CloudMetadataAuthority(store, firstLease);
        await first.ClaimAsync(CancellationToken.None);
        using (var snapshot = CloudControlMetadataSnapshot.Capture(directory.Path, CloudMetadataAuthority.FileNames, new ResourceBudget(1024 * 1024)))
        {
            await first.PublishAsync(snapshot, CancellationToken.None);
        }

        var committed = await store.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None);
        Assert.NotNull(committed);
        // An unrelated mutable upload cannot override the selected immutable generation.
        await store.PutAsync("metadata/FORMAT", "midge-format-version=3\n"u8.ToArray(), new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(20);
        using var secondLease = CreateLease(leaseStore, clock, "second");
        await secondLease.AcquireAsync(CancellationToken.None);
        using var successor = new CloudMetadataAuthority(store, secondLease);
        await successor.ClaimAsync(CancellationToken.None);

        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None);
        Assert.NotNull(files);
        Assert.Equal("midge-format-version=9\n"u8.ToArray(), files["FORMAT"].ToArray());
        Assert.False(files.ContainsKey("manifest.journal"));
        Assert.False(files.ContainsKey("intent_log.json"));
        using var snapshotAfterTakeover = CloudControlMetadataSnapshot.Capture(directory.Path, CloudMetadataAuthority.FileNames, new ResourceBudget(1024 * 1024));
        await Assert.ThrowsAsync<PantsFencedException>(async () => await first.PublishAsync(snapshotAfterTakeover, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldFailClosedWhenInitializedMetadataLosesCommittedPointer()
    {
        var store = new CountingCloudObjectStore();
        await store.PutAsync(CloudMetadataAuthority.SentinelKey, "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var authority = new CloudMetadataAuthority(store, lease);

        await Assert.ThrowsAsync<PantsCorruptionException>(async () => await authority.ClaimAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PantsCorruptionException>(async () => await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectLegacyMutableMetadataDuringInitialization()
    {
        var store = new CountingCloudObjectStore();
        await store.PutAsync("metadata/FORMAT", "midge-format-version=3\n"u8.ToArray(), new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var authority = new CloudMetadataAuthority(store, lease);

        await Assert.ThrowsAsync<PantsCompatibilityException>(async () => await authority.ClaimAsync(CancellationToken.None));
        Assert.Null(await store.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectTamperedImmutableMetadataEvenWhenPointerRemainsValid()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions));
        var store = new CountingCloudObjectStore();
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var authority = new CloudMetadataAuthority(store, lease);
        await authority.ClaimAsync(CancellationToken.None);
        using var snapshot = CloudControlMetadataSnapshot.Capture(directory.Path, CloudMetadataAuthority.FileNames, new ResourceBudget(1024 * 1024));
        await authority.PublishAsync(snapshot, CancellationToken.None);
        var pointer = await store.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None);
        Assert.NotNull(pointer);
        using var decoded = MetadataFrameCodec.Decode(pointer.Data.Span, 4);
        var objectKey = decoded.RootElement.GetProperty("committed").GetProperty("objects")[0].GetProperty("object_key").GetString()!;
        await store.PutAsync(objectKey, "midge-format-version=8\n"u8.ToArray(), new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);

        await Assert.ThrowsAsync<PantsCorruptionException>(async () => await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldReconstructValidatedManifestVersionChain()
    {
        var store = new CountingCloudObjectStore();
        var format = "midge-format-version=9\n"u8.ToArray();
        var manifest = ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions);
        const string operationId = "11111111-1111-1111-1111-111111111111";
        const string key = "manifest/00000000000000000001/" + operationId;
        var changes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["FORMAT"] = new { Replace = format.Select(static value => (int)value).ToArray() },
            ["manifest.snapshot.json"] = new { Replace = manifest.Select(static value => (int)value).ToArray() }
        };
        var version = MetadataFrameCodec.Encode(5, JsonSerializer.SerializeToElement(new
        {
            id = 1,
            writer_epoch = 1,
            operation_id = operationId,
            parent = (object?)null,
            changes
        }));
        await store.PutAsync(key, version, new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        var pointer = MetadataFrameCodec.Encode(4, JsonSerializer.SerializeToElement(new
        {
            writer_epoch = 1,
            holder_id = "writer",
            owner_token = "owner",
            committed = new
            {
                id = 1,
                manifest_horizon = 0,
                objects = new[] { new { file_name = "manifest-chain", object_key = key, len = version.Length, crc32c = DiskFormat.Crc32C(version) } }
            }
        }));
        await store.PutAsync(CloudMetadataAuthority.SentinelKey, "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        await store.PutAsync(CloudMetadataAuthority.PointerKey, pointer, new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);

        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None);

        Assert.NotNull(files);
        Assert.Equal(format, files["FORMAT"].ToArray());
        Assert.Equal(manifest, files["manifest.snapshot.json"].ToArray());
    }

    [Fact]
    public async Task ShouldAdmitCommittedClosureAgainstOneSharedBudgetUntilReadSetIsReleased()
    {
        var store = new CountingCloudObjectStore();
        await CloudTestMetadata.SeedAsync(store, ManifestState.CreateInitial());
        var budget = new ResourceBudget(64 * 1024);
        using (var reads = new ProviderCloudControlMetadataReadSet(store, budget))
        {
            var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None, readSet: reads);
            Assert.NotNull(files);
            Assert.InRange(budget.Current, 1, budget.Limit);
        }

        Assert.Equal(0, budget.Current);
        using (var pressure = budget.Reserve(budget.Limit - 256))
        {
            var gets = store.GetCount;
            await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
                await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None, budget));
            Assert.Equal(gets, store.GetCount);
            Assert.Equal(budget.Limit - 256, budget.Current);
        }

        var retried = await CloudMetadataAuthority.ReadCommittedFilesAsync(store, CancellationToken.None, budget);
        Assert.NotNull(retried);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectMetadataPublicationBeforeUploadsWhenSharedReadBudgetCannotFitAuthority()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions));
        var store = new CountingCloudObjectStore();
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var initializer = new CloudMetadataAuthority(store, lease);
        await initializer.ClaimAsync(CancellationToken.None);
        var baseline = store.PutCount;
        var budget = new ResourceBudget(128);
        using var constrained = new CloudMetadataAuthority(store, lease, budget);
        using var snapshot = CloudControlMetadataSnapshot.Capture(directory.Path, CloudMetadataAuthority.FileNames, new ResourceBudget(1024 * 1024));

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () => await constrained.PublishAsync(snapshot, CancellationToken.None));

        Assert.Equal(baseline, store.PutCount);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectNonemptyLocalCacheWhenCommittedPointerHasNoGeneration()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n"u8.ToArray());
        var manifest = ManifestState.CreateInitial();
        manifest.Files.Add(new FileMeta { Name = "a.sst", ColumnFamilyId = 0 });
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(manifest, JsonOptions));
        var store = new CountingCloudObjectStore();
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var authority = new CloudMetadataAuthority(store, lease);
        await authority.ClaimAsync(CancellationToken.None);
        var before = File.ReadAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"));

        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
            await ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, new CountingCloudObjectStore(),
                new CountingCloudObjectStore(), store, PantsRecoveryPolicy.Strict, lease.ValidateRemoteAsync, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "FORMAT")));
    }

    [Fact]
    public async Task ShouldPreserveVerifiedEmptyInitializationWhenCommittedPointerHasNoGeneration()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(ManifestState.CreateInitial(), JsonOptions));
        var store = new CountingCloudObjectStore();
        using var lease = CreateLease(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch), "writer");
        await lease.AcquireAsync(CancellationToken.None);
        using var authority = new CloudMetadataAuthority(store, lease);
        await authority.ClaimAsync(CancellationToken.None);

        await ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, new CountingCloudObjectStore(),
            new CountingCloudObjectStore(), store, PantsRecoveryPolicy.Strict, lease.ValidateRemoteAsync, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(directory.Path, "FORMAT")));
        Assert.Empty(CloudManifestReader.ReadManifest(directory.Path)!.Files);
    }

    static CloudLeaseCoordinator CreateLease(TestCloudLeaseStore store, ManualClock clock, string holder) =>
        new(store, clock, holder, TimeSpan.FromSeconds(10), TimeSpan.Zero);
}
