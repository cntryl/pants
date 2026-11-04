using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudHydrationLeaseTests
{
    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldFenceLocalMetadataWriteWhenSuccessorTakesLeaseAtFileBoundary()
    {
        using var cache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "predecessor",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var controlStore = new SnapshotConsistencyCloudObjectStore();
        await CloudTestMetadata.SeedAsync(controlStore, ManifestState.CreateInitial());
        var validationCount = 0;

        async ValueTask ValidateRemoteLeaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref validationCount) == 2)
            {
                TakeOver(leaseStore, clock, epoch);
            }

            await lease.ValidateRemoteAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await ProviderCloudPersistence.HydrateLocalCacheAsync(
                cache.Path,
                new SnapshotConsistencyCloudObjectStore(),
                new SnapshotConsistencyCloudObjectStore(),
                controlStore,
                PantsRecoveryPolicy.Strict,
                ValidateRemoteLeaseAsync,
                CancellationToken.None));

        Assert.Equal(2, validationCount);
        Assert.False(File.Exists(Path.Combine(cache.Path, "FORMAT")));
        Assert.False(lease.IsHealthy);
    }

    [Fact]
    public async Task ShouldFenceLocalWalWriteWhenSuccessorTakesLeaseAtFileBoundary()
    {
        using var cache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "predecessor",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var walStore = new SnapshotConsistencyCloudObjectStore();
        var sealedSegment = TestWalSegment.Create(1, epoch, 1, "1.wal", [0xFF]);
        var segmentBytes = sealedSegment.Bytes;
        var segmentKey = PantsCloudObjectLayout.WalSegmentObjectKey(epoch, 1);
        walStore.Seed(segmentKey, segmentBytes);
        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProviderWalCatalog
            {
                FencingEpoch = epoch,
                Segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>
                {
                    [1] = TestWalSegment.Publish(sealedSegment) with
                    {
                        ContentCrc32C = DiskFormat.Crc32C(segmentBytes) ^ 1U
                    }
                }
            },
            CatalogJsonOptions);
        walStore.Seed(PantsCloudObjectLayout.WalCatalogObjectKey, catalogBytes);
        walStore.Seed(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, catalogBytes);
        var validationCount = 0;

        async ValueTask ValidateRemoteLeaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref validationCount) == 2)
            {
                TakeOver(leaseStore, clock, epoch);
            }

            await lease.ValidateRemoteAsync(cancellationToken);
        }

        var localSegmentPath = Path.Combine(
            cache.Path,
            "wal",
            "00000000000000000001.wal");
        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await ProviderCloudPersistence.HydrateLocalCacheAsync(
                cache.Path,
                walStore,
                new SnapshotConsistencyCloudObjectStore(),
                new SnapshotConsistencyCloudObjectStore(),
                PantsRecoveryPolicy.Salvage,
                ValidateRemoteLeaseAsync,
                CancellationToken.None));

        Assert.Equal(2, validationCount);
        Assert.False(File.Exists(localSegmentPath));
        Assert.False(lease.IsHealthy);
    }

    static void TakeOver(TestCloudLeaseStore leaseStore, ManualClock clock, ulong epoch) =>
        leaseStore.Seed(new CloudLeaseRecord(
            "successor",
            epoch + 1,
            "successor-token",
            clock.UtcNow,
            clock.UtcNow + TimeSpan.FromSeconds(10)));
}
