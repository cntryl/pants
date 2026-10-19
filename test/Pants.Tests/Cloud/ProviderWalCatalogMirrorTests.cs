using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderWalCatalogMirrorTests
{
    const string Primary = PantsCloudObjectLayout.WalCatalogObjectKey;
    const string Mirror = PantsCloudObjectLayout.WalCatalogMirrorObjectKey;

    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldConvergeMirrorToPrimaryAfterEveryCatalogCommit()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);

        var primary = await ReadAsync(walStore, Primary);
        var mirror = await ReadAsync(walStore, Mirror);

        Assert.NotNull(primary);
        Assert.Equal(primary, mirror);
    }

    [Fact]
    public async Task ShouldRepairTornPrimaryFromValidMirrorAndRecoverSegments()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        var expected = (await ReadAsync(walStore, Primary))!;
        await WriteAsync(walStore, Primary, expected.AsSpan(0, expected.Length / 2).ToArray());

        var hydrated = await HydrateAsync(cache.Path, walStore);

        Assert.Equal([1UL], hydrated.PublishedWalSegments.Keys);
        Assert.Equal(expected, await ReadAsync(walStore, Primary));
    }

    [Fact]
    public async Task ShouldRestoreMissingPrimaryFromValidMirror()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        var expected = (await ReadAsync(walStore, Primary))!;
        _ = await walStore.DeleteAsync(
            Primary,
            new PantsCloudObjectDeleteCondition.Unconditional(),
            CancellationToken.None);

        var hydrated = await HydrateAsync(cache.Path, walStore);

        Assert.Equal([1UL], hydrated.PublishedWalSegments.Keys);
        Assert.Equal(expected, await ReadAsync(walStore, Primary));
    }

    [Fact]
    public async Task ShouldFailClosedWithoutMutatingEitherCopyWhenBothCatalogsAreInvalid()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        await WriteAsync(walStore, Primary, "{ torn"u8.ToArray());
        await WriteAsync(walStore, Mirror, "{ also torn"u8.ToArray());

        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
            await HydrateAsync(cache.Path, walStore));

        Assert.Equal("{ torn"u8.ToArray(), await ReadAsync(walStore, Primary));
        Assert.Equal("{ also torn"u8.ToArray(), await ReadAsync(walStore, Mirror));
    }

    [Fact]
    public async Task ShouldRewriteStaleMirrorGivenValidPrimary()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        var expected = (await ReadAsync(walStore, Primary))!;
        await WriteAsync(walStore, Mirror, "stale"u8.ToArray());

        _ = await HydrateAsync(cache.Path, walStore);

        Assert.Equal(expected, await ReadAsync(walStore, Mirror));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNotRepairCatalogCopyWhenSharedPoolCannotAdmitRepairPublication(
        bool primaryIsTorn)
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
        var segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>();
        for (ulong segmentId = 1; segmentId <= 400; segmentId++)
        {
            segments.Add(segmentId, new ProviderPublishedWalSegment
            {
                SegmentId = segmentId,
                WriterEpoch = epoch,
                MaximumSequence = segmentId,
                SizeBytes = 1,
                ContentCrc32C = 0,
                ObjectKey = PantsCloudObjectLayout.WalSegmentObjectKey(epoch, segmentId)
            });
        }

        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProviderWalCatalog { FencingEpoch = epoch, Segments = segments },
            CatalogJsonOptions);
        var walStore = new ScriptedCloudObjectStore();
        var originalPrimary = primaryIsTorn ? "{ torn"u8.ToArray() : catalogBytes;
        walStore.Write(Primary, originalPrimary);
        if (primaryIsTorn)
        {
            walStore.Write(Mirror, catalogBytes);
        }

        var encodedReadBytes = checked(
            (long)originalPrimary.Length + (primaryIsTorn ? catalogBytes.Length : 0));
        var readWorkspaceBytes = checked(encodedReadBytes * 16);
        var budget = new ResourceBudget(readWorkspaceBytes + 128 * 1024);
        var persistence = new ProviderCloudPersistence(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            lease,
            walRetirementSettings: new WalRetirementSettings(
                budget,
                new MaintenanceMemoryGate(),
                WalRetirementSettings.DefaultPageBytes,
                WalRetirementSettings.DefaultQuantum,
                TimeProvider.System));

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            persistence.FenceWalCatalogAsync(CancellationToken.None).AsTask());

        Assert.Equal(originalPrimary, walStore.Read(Primary));
        if (primaryIsTorn)
        {
            Assert.Equal(catalogBytes, walStore.Read(Mirror));
        }
        else
        {
            Assert.False(walStore.Contains(Mirror));
        }
        Assert.Equal(0, budget.Current);
        Assert.InRange(budget.Peak, 0, budget.Limit);
    }

    [Fact]
    public async Task ShouldRejectOpenWhenWalSegmentsExistButBothCatalogCopiesAreMissing()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        foreach (var key in new[] { Primary, Mirror })
        {
            _ = await walStore.DeleteAsync(
                key,
                new PantsCloudObjectDeleteCondition.Unconditional(),
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await HydrateAsync(cache.Path, walStore));

        Assert.Contains(Primary, exception.Message, StringComparison.Ordinal);
        Assert.Null(await ReadAsync(walStore, Primary));
    }

    [Fact]
    public async Task ShouldReportCatalogEpochAsTheLeaseFloorAndZeroForAFreshBucket()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        var catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { });
        Assert.Equal(0UL, await catalog.ReadFencingEpochAsync(CancellationToken.None));

        await PublishAsync(cache.Path, walStore);

        Assert.True(await catalog.ReadFencingEpochAsync(CancellationToken.None) >= 1UL);
    }

    [Fact]
    public async Task ShouldReadCatalogCopiesThroughBoundedRanges()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        var getsBeforeRead = walStore.GetCount;
        var rangesBeforeRead = walStore.RangeGetCount;
        var catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { });

        using var read = await catalog.ReadAsync(false, CancellationToken.None);

        Assert.NotNull(read.Catalog);
        Assert.Equal(getsBeforeRead, walStore.GetCount);
        Assert.True(walStore.RangeGetCount > rangesBeforeRead);
        Assert.InRange(walStore.LargestRangeBytes, 1, 64 * 1024);
    }

    [Fact]
    public async Task ShouldRejectOversizedCatalogBeforeRangeReadOrRepair()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        var oversized = new byte[4096];
        await WriteAsync(walStore, Primary, oversized);
        await WriteAsync(walStore, Mirror, "{}"u8.ToArray());
        var getsBeforeRead = walStore.GetCount;
        var rangesBeforeRead = walStore.RangeGetCount;
        var putsBeforeRead = walStore.PutCount;
        var budget = new ResourceBudget(1024);
        var catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { },
            new ProviderWalCatalogReadAdmission(budget, new MaintenanceMemoryGate()));

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await catalog.ReadAsync(false, CancellationToken.None));

        Assert.Equal(getsBeforeRead, walStore.GetCount);
        Assert.Equal(rangesBeforeRead, walStore.RangeGetCount);
        Assert.Equal(putsBeforeRead, walStore.PutCount);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldKeepCatalogMemoryChargedUntilCallerDisposesRead()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new CountingCloudObjectStore();
        await PublishAsync(cache.Path, walStore);
        var primaryMetadata = await walStore.HeadAsync(Primary, CancellationToken.None);
        var mirrorMetadata = await walStore.HeadAsync(Mirror, CancellationToken.None);
        Assert.NotNull(primaryMetadata);
        Assert.NotNull(mirrorMetadata);
        var required = checked(
            (long)(primaryMetadata.SizeBytes + mirrorMetadata.SizeBytes) * 16);
        var budget = new ResourceBudget(required);
        var catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { },
            new ProviderWalCatalogReadAdmission(budget, new MaintenanceMemoryGate()));

        var read = await catalog.ReadAsync(false, CancellationToken.None);
        Assert.NotNull(read.Catalog);
        Assert.Equal(required, budget.Current);
        var rangesAfterFirstRead = walStore.RangeGetCount;

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await catalog.ReadAsync(false, CancellationToken.None));

        Assert.Equal(rangesAfterFirstRead, walStore.RangeGetCount);
        Assert.Equal(required, budget.Current);

        read.Dispose();

        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldOpenFreshBucketWithNeitherCatalogNorWalObjects()
    {
        using var cache = new TemporaryDirectory();

        var hydrated = await HydrateAsync(cache.Path, new CountingCloudObjectStore());

        Assert.Empty(hydrated.PublishedWalSegments);
    }

    static async Task PublishAsync(string cachePath, ICloudObjectStore walStore)
    {
        var lease = new CloudLeaseCoordinator(
            new TestCloudLeaseStore(),
            new ManualClock(DateTimeOffset.UnixEpoch),
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        using (lease)
        {
            var epoch = await lease.AcquireAsync(CancellationToken.None);
            var persistence = new ProviderCloudPersistence(
                cachePath,
                walStore,
                new TestCloudObjectStore(),
                new TestCloudObjectStore(),
                lease);
            await persistence.PublishWalBatchAsync(
                [new SealedWalSegment(1, epoch, 1, "1.wal", [1])],
                CancellationToken.None);
        }
    }

    static async ValueTask<ProviderCloudHydrationResult> HydrateAsync(
        string cachePath,
        ICloudObjectStore walStore)
    {
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        return await ProviderCloudPersistence.HydrateLocalCacheAsync(
            cachePath,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            PantsRecoveryPolicy.Strict,
            CancellationToken.None,
            catalogReadAdmission: new ProviderWalCatalogReadAdmission(budget, gate),
            catalogWriteAdmission: new ProviderWalCatalogWriteAdmission(budget));
    }

    static async Task<byte[]?> ReadAsync(CountingCloudObjectStore store, string key) =>
        (await store.GetAsync(key, CancellationToken.None))?.Data.ToArray();

    static async Task WriteAsync(CountingCloudObjectStore store, string key, byte[] data) =>
        Assert.True(await store.PutAsync(
            key,
            data,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));
}
