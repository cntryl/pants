using System.Text.Json;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudControlMetadataReadTests
{
    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldRejectHydrationBeforeReadingControlMetadataWhenWorkspaceExceedsSharedBudget()
    {
        using var cache = new TemporaryDirectory();
        var controlStore = new ScriptedCloudObjectStore();
        controlStore.Write(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json",
            new byte[1024]);
        var budget = new ResourceBudget(512);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            ProviderCloudPersistence.HydrateLocalCacheAsync(
                    cache.Path,
                    new ScriptedCloudObjectStore(),
                    new ScriptedCloudObjectStore(),
                    controlStore,
                    PantsRecoveryPolicy.Strict,
                    static _ => ValueTask.CompletedTask,
                    CancellationToken.None,
                    controlMetadataMemoryBudget: budget)
                .AsTask());

        Assert.Equal(0, budget.Current);
        Assert.Contains(controlStore.RequestsFor(
                PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json"),
            static request => request.Operation == StoreOperation.Head);
        Assert.DoesNotContain(controlStore.Requests,
            static request => request.Operation is StoreOperation.Get or StoreOperation.GetRange or
                StoreOperation.Put or StoreOperation.Delete);
    }

    [Fact]
    public async Task ShouldKeepSmallWholeObjectFallbackChargedUntilCallerReleasesIt()
    {
        const string objectKey = "metadata/test.json";
        byte[] expected = [1, 2, 3, 4];
        var store = new ScriptedCloudObjectStore { SupportsRanges = false };
        store.Write(objectKey, expected);
        var budget = new ResourceBudget(1024);

        using (var reads = new ProviderCloudControlMetadataReadSet(store, budget))
        {
            var value = await reads.ReadAsync(objectKey, CancellationToken.None);

            Assert.NotNull(value);
            Assert.Equal(expected, value.Data.ToArray());
            Assert.Equal(320, budget.Current);
            Assert.Contains(store.RequestsFor(objectKey),
                static request => request.Operation == StoreOperation.Head);
            Assert.Contains(store.RequestsFor(objectKey),
                static request => request.Operation == StoreOperation.Get);
        }

        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectConcurrentControlReadBeforeBodyWhenSharedPoolIsOccupied()
    {
        const string objectKey = "metadata/test.json";
        var store = new ScriptedCloudObjectStore();
        store.Write(objectKey, new byte[32]);
        var budget = new ResourceBudget(1000);
        var rangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rangeCalls = 0;
        store.BeforeRangeRead = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref rangeCalls) == 1)
            {
                rangeStarted.TrySetResult();
                await releaseRange.Task.WaitAsync(cancellationToken);
            }
        };
        using var firstRead = new ProviderCloudControlMetadataReadSet(store, budget);
        using var secondRead = new ProviderCloudControlMetadataReadSet(store, budget);
        var firstTask = firstRead.ReadAsync(objectKey, CancellationToken.None).AsTask();

        await rangeStarted.Task.WaitAsync(TestTimeouts.Expected);
        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            secondRead.ReadAsync(objectKey, CancellationToken.None).AsTask());

        releaseRange.TrySetResult();
        var value = await firstTask;
        Assert.NotNull(value);
        Assert.Equal(768, budget.Current);
        Assert.Equal(1, store.RequestsFor(objectKey)
            .Count(static request => request.Operation == StoreOperation.GetRange));

        firstRead.Dispose();
        Assert.Equal(0, budget.Current);
        using var retryRead = new ProviderCloudControlMetadataReadSet(store, budget);
        Assert.NotNull(await retryRead.ReadAsync(objectKey, CancellationToken.None));
        Assert.Equal(768, budget.Current);
    }

    [Fact]
    public async Task ShouldReleaseReservationWhenObjectChangesAfterMetadataRead()
    {
        const string objectKey = "metadata/test.json";
        var store = new ScriptedCloudObjectStore();
        store.Write(objectKey, new byte[32]);
        store.BeforeRangeRead = (_, _) =>
        {
            store.Write(objectKey, new byte[32]);
            return ValueTask.CompletedTask;
        };
        var budget = new ResourceBudget(1024);

        using var reads = new ProviderCloudControlMetadataReadSet(store, budget);
        await Assert.ThrowsAsync<PantsBusyException>(() =>
            reads.ReadAsync(objectKey, CancellationToken.None).AsTask());

        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(store.Requests,
            static request => request.Operation is StoreOperation.Put or StoreOperation.Delete);
    }

    [Fact]
    public async Task ShouldReleaseReservationWhenRangeReadIsCancelled()
    {
        const string objectKey = "metadata/test.json";
        var store = new ScriptedCloudObjectStore();
        store.Write(objectKey, new byte[32]);
        var rangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRangeRead = async (_, cancellationToken) =>
        {
            rangeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var budget = new ResourceBudget(1024);
        using var reads = new ProviderCloudControlMetadataReadSet(store, budget);
        using var cancellation = new CancellationTokenSource();
        var read = reads.ReadAsync(objectKey, cancellation.Token).AsTask();

        await rangeStarted.Task.WaitAsync(TestTimeouts.Expected);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldReleaseAdmittedControlMetadataAfterDecodeFailure()
    {
        using var cache = new TemporaryDirectory();
        var controlStore = new ScriptedCloudObjectStore();
        controlStore.Write(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json",
            new byte[32]);
        var budget = new ResourceBudget(4096);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            ProviderCloudPersistence.HydrateLocalCacheAsync(
                    cache.Path,
                    new ScriptedCloudObjectStore(),
                    new ScriptedCloudObjectStore(),
                    controlStore,
                    PantsRecoveryPolicy.Strict,
                    static _ => ValueTask.CompletedTask,
                    CancellationToken.None,
                    controlMetadataMemoryBudget: budget)
                .AsTask());

        Assert.Equal(0, budget.Current);
        Assert.Contains(controlStore.Requests,
            static request => request.Operation == StoreOperation.GetRange);
        Assert.DoesNotContain(controlStore.Requests,
            static request => request.Operation is StoreOperation.Put or StoreOperation.Delete);
    }

    [Fact]
    public async Task ShouldReleaseReservationWhenProviderReturnsAShortRange()
    {
        const string objectKey = "metadata/test.json";
        var store = new ScriptedCloudObjectStore();
        store.Write(objectKey, new byte[32]);
        var budget = new ResourceBudget(1024);

        using var reads = new ProviderCloudControlMetadataReadSet(
            new ShortRangeStore(store),
            budget);
        await Assert.ThrowsAsync<PantsIOException>(() =>
            reads.ReadAsync(objectKey, CancellationToken.None).AsTask());

        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(store.Requests,
            static request => request.Operation is StoreOperation.Put or StoreOperation.Delete);
    }

    [Fact]
    public async Task ShouldRejectWholeObjectFallbackAboveItsSmallObjectLimit()
    {
        const string objectKey = "metadata/manifest.snapshot.json";
        using var cache = new TemporaryDirectory();
        var controlStore = new ScriptedCloudObjectStore { SupportsRanges = false };
        controlStore.Write(objectKey, new byte[ProviderCloudControlMetadataReadSet.MaximumWholeObjectBytes + 1]);
        var budget = new ResourceBudget(2 * 1024 * 1024);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            ProviderCloudPersistence.HydrateLocalCacheAsync(
                    cache.Path,
                    new ScriptedCloudObjectStore(),
                    new ScriptedCloudObjectStore(),
                    controlStore,
                    PantsRecoveryPolicy.Strict,
                    static _ => ValueTask.CompletedTask,
                    CancellationToken.None,
                    controlMetadataMemoryBudget: budget)
                .AsTask());

        Assert.Equal(0, budget.Current);
        Assert.Contains(controlStore.RequestsFor(objectKey),
            static request => request.Operation == StoreOperation.Head);
        Assert.DoesNotContain(controlStore.Requests,
            static request => request.Operation is StoreOperation.Get or StoreOperation.Put or
                StoreOperation.Delete);
    }

    [Fact]
    public async Task ShouldNotRepairWalCatalogWhenSalvageCannotAdmitControlMetadata()
    {
        using var cache = new TemporaryDirectory();
        var controlStore = new ScriptedCloudObjectStore();
        var manifestKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json";
        controlStore.Write(manifestKey, new byte[1024]);
        var walStore = new ScriptedCloudObjectStore();
        walStore.Write(
            PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
            JsonSerializer.SerializeToUtf8Bytes(
                new ProviderWalCatalog { FencingEpoch = 1 },
                CatalogJsonOptions));
        var budget = new ResourceBudget(8 * 1024);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            ProviderCloudPersistence.HydrateLocalCacheAsync(
                    cache.Path,
                    walStore,
                    new ScriptedCloudObjectStore(),
                    controlStore,
                    PantsRecoveryPolicy.Salvage,
                    static _ => ValueTask.CompletedTask,
                    CancellationToken.None,
                    catalogReadAdmission: new ProviderWalCatalogReadAdmission(
                        budget,
                        new MaintenanceMemoryGate()),
                    catalogWriteAdmission: new CloudMetadataWriteAdmission(budget),
                    controlMetadataMemoryBudget: budget)
                .AsTask());

        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(controlStore.Requests,
            static request => request.Operation is StoreOperation.Get or StoreOperation.GetRange or
                StoreOperation.Put or StoreOperation.Delete);
        Assert.DoesNotContain(walStore.Requests,
            static request => request.Operation is StoreOperation.Put or StoreOperation.Delete);
        Assert.True(walStore.Contains(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
        Assert.False(walStore.Contains(PantsCloudObjectLayout.WalCatalogObjectKey));
    }

    sealed class ShortRangeStore(ICloudObjectStore inner) : ICloudObjectStore
    {
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask<CloudObject?> GetAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(key, cancellationToken);

        public async ValueTask<CloudObject?> GetRangeAsync(
            string key,
            ulong offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var value = await inner.GetRangeAsync(key, offset, length, cancellationToken)
                .ConfigureAwait(false);
            return value is null
                ? null
                : value with { Data = value.Data[..(length - 1)] };
        }

        public ValueTask<CloudObjectMetadata?> HeadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            inner.HeadAsync(key, cancellationToken);

        public ValueTask<bool> PutAsync(
            string key,
            ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition,
            CancellationToken cancellationToken = default) =>
            inner.PutAsync(key, data, condition, cancellationToken);

        public ValueTask<CloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken = default) =>
            inner.ListPageAsync(prefix, continuationToken, cancellationToken);

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
            string key,
            CloudObjectDeleteCondition condition,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(key, condition, cancellationToken);
    }
}
