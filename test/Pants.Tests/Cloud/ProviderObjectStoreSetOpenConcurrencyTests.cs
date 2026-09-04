using System.Diagnostics;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderObjectStoreSetOpenConcurrencyTests
{
    static readonly TimeSpan PerStoreDelay = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task OpenAsyncOpensTheThreeStoresConcurrentlyRatherThanSequentially()
    {
        var wal = new DelayedProvider("wal-provider", PerStoreDelay);
        var sst = new DelayedProvider("sst-provider", PerStoreDelay);
        var control = new DelayedProvider("control-provider", PerStoreDelay);
        var topology = new PantsCloudStorageTopology(
            new PantsCloudStorageLocation(wal, "wal"),
            new PantsCloudStorageLocation(sst, "sst"),
            new PantsCloudStorageLocation(control, "control"));

        var stopwatch = Stopwatch.StartNew();
        await using var stores = await ProviderObjectStoreSet.OpenAsync(
            topology,
            TimeSpan.FromSeconds(30),
            null,
            TimeProvider.System,
            CancellationToken.None);
        stopwatch.Stop();

        // Sequential opens would take roughly 3x the per-store delay; concurrent opens should
        // finish close to a single delay's duration.
        Assert.True(
            stopwatch.Elapsed < PerStoreDelay * 2,
            $"Expected opens to run concurrently (< {PerStoreDelay * 2}), took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task OpenAsyncDisposesAlreadyOpenedStoresWhenAnotherOpenFails()
    {
        var wal = new DelayedProvider("wal-provider", TimeSpan.FromMilliseconds(10));
        var sst = new DelayedProvider("sst-provider", TimeSpan.FromMilliseconds(10));
        var control = new DelayedProvider(
            "control-provider",
            TimeSpan.FromMilliseconds(150),
            throwOnOpen: true);
        var topology = new PantsCloudStorageTopology(
            new PantsCloudStorageLocation(wal, "wal"),
            new PantsCloudStorageLocation(sst, "sst"),
            new PantsCloudStorageLocation(control, "control"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderObjectStoreSet.OpenAsync(
            topology,
            TimeSpan.FromSeconds(30),
            null,
            TimeProvider.System,
            CancellationToken.None).AsTask());

        Assert.NotNull(wal.OpenedStore);
        Assert.NotNull(sst.OpenedStore);
        Assert.Equal(1, wal.OpenedStore!.DisposeCount);
        Assert.Equal(1, sst.OpenedStore!.DisposeCount);
    }

    sealed class DelayedProvider(string id, TimeSpan delay, bool throwOnOpen = false) : IPantsCloudProvider
    {
        public DisposalTrackingStore? OpenedStore { get; private set; }

        public PantsCloudProviderId Id { get; } = new(id);

        public PantsCloudValidationReport Validate() => new([]);

        public async ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
            PantsCloudProviderContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (throwOnOpen)
            {
                throw new InvalidOperationException("Simulated open failure.");
            }

            OpenedStore = new DisposalTrackingStore();
            return OpenedStore;
        }
    }

    sealed class DisposalTrackingStore : IPantsCloudObjectStore
    {
        public int DisposeCount { get; private set; }

        public ValueTask<PantsCloudObject?> GetAsync(
            string objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by this test double.");

        public ValueTask<PantsCloudObjectMetadata?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by this test double.");

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            PantsCloudObjectWriteCondition condition,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by this test double.");

        public ValueTask<PantsCloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by this test double.");

        public ValueTask<PantsCloudObjectDeleteOutcome> DeleteAsync(
            string objectKey,
            PantsCloudObjectDeleteCondition condition,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by this test double.");

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
