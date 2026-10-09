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
    ///     A flush is the only thing that turns a full memtable into an SST, and writes stall on
    ///     level-0 debt, so refusing it for space would wedge the engine rather than degrade it. It
    ///     evicts to make room and, failing that, proceeds with its bounded, memtable-sized output.
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
    public async Task ShouldProceedWithFlushWhenEvictionCannotFreeEnough()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300, evictable: false);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        using var reservation = await manager.ReserveFlushAsync(store, 5000, CancellationToken.None);

        Assert.Equal(5000, manager.Ledger.ReservedBytes);
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
