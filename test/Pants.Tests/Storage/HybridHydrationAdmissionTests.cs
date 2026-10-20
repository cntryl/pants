using Cntryl.Pants.Support.Failpoints;

namespace Cntryl.Pants.Storage;

public sealed class HybridHydrationAdmissionTests
{
    /// <summary>
    ///     Hydration grows local disk without the caller asking for a write, so reading enough cold
    ///     SSTs used to push straight past the operator's cap. It must be refused before the
    ///     download, not noticed afterwards.
    /// </summary>
    [Fact]
    public async Task ShouldRefuseHydrationBeforeDownloadingBeyondTheLocalBudget()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 400);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);

        await manager.EnsureLocalSstsAsync(store, ["a.sst"], CancellationToken.None);
        await manager.EnsureLocalSstsAsync(store, ["b.sst"], CancellationToken.None);

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await manager.EnsureLocalSstsAsync(store, ["c.sst"], CancellationToken.None));

        Assert.Equal(["a.sst", "b.sst"], store.Hydrated);
    }

    [Fact]
    public async Task ShouldHydrateWhenTheFileFitsWithinTheRemainingBudget()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 100);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);

        await manager.EnsureLocalSstsAsync(store, ["a.sst", "b.sst"], CancellationToken.None);

        Assert.Equal(["a.sst", "b.sst"], store.Hydrated);
    }

    /// <summary>
    ///     A flush must fit in capacity already preserved for its output. If eviction cannot make
    ///     that capacity available, admitting the output anyway would violate the local budget.
    /// </summary>
    [Fact]
    public async Task ShouldEvictToAdmitFlushRatherThanRefuseIt()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        using var reservation = await manager.ReserveFlushAsync(store, 400, CancellationToken.None);

        Assert.NotEmpty(store.Evicted);
        Assert.Equal(400, manager.Ledger.ReservedBytes);
    }

    [Fact]
    public async Task ShouldRejectFlushWhenEvictionCannotFreeEnough()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300, evictable: false);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await manager.ReserveFlushAsync(store, 5000, CancellationToken.None));

        Assert.Equal(0, manager.Ledger.ReservedBytes);
        Assert.Equal(900, manager.Ledger.ChargedBytes);
    }

    /// <summary>
    ///     Compaction used to be admitted unconditionally here, overshooting the budget by its whole
    ///     input and output set. It now stages outputs through a fixed window and streams inputs
    ///     remotely, so a window that cannot be made to fit is refused with a typed error instead.
    /// </summary>
    [Fact]
    public async Task ShouldRefuseCompactionStagingWithNoSpaceWhenEvictionCannotFreeEnough()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300, evictable: false);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await manager.ReserveCompactionStagingAsync(store, 500, CancellationToken.None));

        Assert.Equal(0, manager.Ledger.ReservedBytes);
    }

    [Fact]
    public async Task ShouldEvictBelowTheHighWatermarkUntilTheCompactionStagingWindowFits()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 100);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst", "d.sst", "e.sst", "f.sst", "g.sst"],
            CancellationToken.None);

        using var reservation = await manager.ReserveCompactionStagingAsync(
            store,
            500,
            CancellationToken.None);

        Assert.Equal(["a.sst", "b.sst"], store.Evicted);
        Assert.Equal(500, manager.Ledger.ReservedBytes);
        Assert.True(manager.Ledger.ChargedBytes <= 1000);
    }

    [Fact]
    public async Task ShouldReportOnlyPlannedSstsWhileHighWatermarkEvictionIsBlocked()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 90);
        var failpoints = new BlockingHybridEvictionFailpointHandler();
        using var manager = new HybridCacheManager(1000, failpoints);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            Enumerable.Range(0, 10).Select(static index => $"{index}.sst"),
            CancellationToken.None);
        failpoints.Arm();

        var eviction = Task.Run(async () =>
            await manager.EvictIfNeededAsync(store, CancellationToken.None));
        try
        {
            await failpoints.WaitUntilBlockedAsync(TestTimeouts.Expected);

            var pending = manager.GetMetrics(store).PendingEvictions;

            Assert.Equal(1, pending);
            Assert.True(pending < store.Hydrated.Count);
        }
        finally
        {
            failpoints.Release();
        }

        await eviction;

        Assert.Single(store.Evicted);
        Assert.Equal(0, manager.GetMetrics(store).PendingEvictions);
    }

    [Fact]
    public async Task ShouldNotEvictProtectedCompactionInputsToFitTheStagingWindow()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);
        using var protection = await manager.ProtectSstsFromEvictionAsync(
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await manager.ReserveCompactionStagingAsync(store, 500, CancellationToken.None));

        Assert.Empty(store.Evicted);
    }

    sealed class FakeHybridCacheStore(long sstSizeBytes, bool evictable = true) : IHybridCacheStore
    {
        public List<string> Hydrated { get; } = [];

        public List<string> Evicted { get; } = [];

        public long LocalCommittedBytes => Hydrated.Count * sstSizeBytes;

        public long LocalWalBytes => 0;

        public long LocalSstBytes => LocalCommittedBytes;

        public long LocalStartupResidueBytes => 0;

        public IReadOnlyList<HybridLocalSst> GetLocalManifestSsts() =>
            evictable
                ? Hydrated.Select(name => new HybridLocalSst(name, sstSizeBytes)).ToArray()
                : [];

        public bool TryGetManifestSstSizeBytes(string name, out long sizeBytes)
        {
            sizeBytes = sstSizeBytes;
            return true;
        }

        public bool IsSstLocal(string name) => Hydrated.Contains(name, StringComparer.Ordinal);

        public ValueTask VerifyRemoteSstMatchesLocalAsync(
            string name,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void EvictLocalSst(string name)
        {
            Hydrated.Remove(name);
            Evicted.Add(name);
        }

        public ValueTask HydrateLocalSstAsync(string name, CancellationToken cancellationToken)
        {
            Hydrated.Add(name);
            return ValueTask.CompletedTask;
        }
    }
}
