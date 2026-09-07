using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudWalRecoveryStreamingTests
{
    /// <summary>
    ///     Recovery reads every published WAL segment. Fetching each one whole makes peak memory a
    ///     function of how far pruning had fallen behind before the crash, which is exactly the
    ///     situation recovery has to survive on a small instance.
    /// </summary>
    [Fact]
    public async Task ShouldReadPublishedWalSegmentsInBoundedPages()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new RangeTrackingCloudObjectStore();
        await PublishSegmentAsync(
            cache.Path,
            walStore,
            new byte[(ProviderCloudPersistence.WalRecoveryPageBytes * 3) + 17]);

        // Publication reads the segment back to verify it; only recovery is under test here.
        walStore.ResetReadTracking();

        var hydrated = await ProviderCloudPersistence.HydrateLocalCacheAsync(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);

        Assert.Single(hydrated.PublishedWalSegments);
        Assert.True(
            walStore.LargestSingleReadBytes <= ProviderCloudPersistence.WalRecoveryPageBytes,
            $"Recovery read {walStore.LargestSingleReadBytes} bytes in one call, above the " +
            $"{ProviderCloudPersistence.WalRecoveryPageBytes}-byte page.");
    }

    /// <summary>
    ///     Ranged reads are optional on the public object-store contract, so a custom store may
    ///     refuse them. Recovery must still work, just without the bound.
    /// </summary>
    [Fact]
    public async Task ShouldRecoverGivenAnObjectStoreWithoutRangedReads()
    {
        using var cache = new TemporaryDirectory();
        var walStore = new RangeTrackingCloudObjectStore { SupportsRanges = false };
        await PublishSegmentAsync(cache.Path, walStore, new byte[4096]);

        var hydrated = await ProviderCloudPersistence.HydrateLocalCacheAsync(
            cache.Path,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);

        Assert.Single(hydrated.PublishedWalSegments);
    }

    static async Task PublishSegmentAsync(
        string cachePath,
        ICloudObjectStore walStore,
        byte[] payload)
    {
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var persistence = new ProviderCloudPersistence(
            cachePath,
            walStore,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            lease);

        await persistence.PublishWalBatchAsync(
            [new SealedWalSegment(1, epoch, 1, "1.wal", payload)],
            CancellationToken.None);
    }

    sealed class RangeTrackingCloudObjectStore : ICloudObjectStore
    {
        readonly Dictionary<string, (byte[] Data, string Version)> _objects =
            new(StringComparer.Ordinal);

        public bool SupportsRanges { get; init; } = true;

        public int LargestSingleReadBytes { get; private set; }

        public void ResetReadTracking() => LargestSingleReadBytes = 0;

        public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken)
        {
            if (_objects.TryGetValue(objectKey, out var value))
            {
                LargestSingleReadBytes = Math.Max(LargestSingleReadBytes, value.Data.Length);
                return ValueTask.FromResult<CloudObject?>(
                    new CloudObject(value.Data, value.Version));
            }

            return ValueTask.FromResult<CloudObject?>(null);
        }

        public ValueTask<CloudObject?> GetRangeAsync(
            string objectKey,
            ulong offset,
            int length,
            CancellationToken cancellationToken)
        {
            if (!SupportsRanges)
            {
                throw new PantsNotSupportedException("Ranged reads are not supported.");
            }

            if (!_objects.TryGetValue(objectKey, out var value))
            {
                return ValueTask.FromResult<CloudObject?>(null);
            }

            var start = checked((int)offset);
            var count = Math.Max(0, Math.Min(length, value.Data.Length - start));
            LargestSingleReadBytes = Math.Max(LargestSingleReadBytes, count);
            return ValueTask.FromResult<CloudObject?>(
                new CloudObject(value.Data.AsMemory(start, count).ToArray(), value.Version));
        }

        public ValueTask<CloudObjectMetadata?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
                ? new CloudObjectMetadata(checked((ulong)value.Data.Length), value.Version, null, null)
                : null);

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition,
            CancellationToken cancellationToken)
        {
            var exists = _objects.TryGetValue(objectKey, out var current);
            var accepted = condition switch
            {
                PantsCloudObjectWriteCondition.Unconditional => true,
                PantsCloudObjectWriteCondition.IfAbsent => !exists,
                PantsCloudObjectWriteCondition.IfVersion expected =>
                    exists && StringComparer.Ordinal.Equals(expected.Version, current.Version),
                _ => false
            };
            if (!accepted)
            {
                return ValueTask.FromResult(false);
            }

            _objects[objectKey] = (data.ToArray(), Guid.NewGuid().ToString("N"));
            return ValueTask.FromResult(true);
        }

        public ValueTask<CloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CloudObjectListPage(
                continuationToken is null
                    ? _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                        .ToArray()
                    : [],
                null));

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
            string objectKey,
            CloudObjectDeleteCondition condition,
            CancellationToken cancellationToken)
        {
            _objects.Remove(objectKey);
            return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
        }
    }
}
