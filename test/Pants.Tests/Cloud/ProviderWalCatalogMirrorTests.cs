using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderWalCatalogMirrorTests
{
    const string Primary = PantsCloudObjectLayout.WalCatalogObjectKey;
    const string Mirror = PantsCloudObjectLayout.WalCatalogMirrorObjectKey;

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

    static ValueTask<ProviderCloudHydrationResult> HydrateAsync(
        string cachePath,
        ICloudObjectStore walStore) =>
        ProviderCloudPersistence.HydrateLocalCacheAsync(
            cachePath,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);

    static async Task<byte[]?> ReadAsync(CountingCloudObjectStore store, string key) =>
        (await store.GetAsync(key, CancellationToken.None))?.Data.ToArray();

    static async Task WriteAsync(CountingCloudObjectStore store, string key, byte[] data) =>
        Assert.True(await store.PutAsync(
            key,
            data,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));
}
