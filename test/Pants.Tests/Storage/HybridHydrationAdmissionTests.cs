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
    ///     Compaction and flush are the only things that reduce L0 debt, and writes now stall on
    ///     that debt. Refusing them for space would therefore not degrade the engine, it would wedge
    ///     it permanently — so maintenance evicts to make room and, failing that, proceeds anyway.
    /// </summary>
    [Fact]
    public async Task ShouldEvictToAdmitMaintenanceRatherThanRefuseIt()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        using var reservation = await manager.ReserveForMaintenanceAsync(
            store,
            StorageAdmissionKind.Compaction,
            400,
            CancellationToken.None);

        Assert.NotEmpty(store.Evicted);
        Assert.Equal(400, manager.Ledger.ReservedBytes);
    }

    [Fact]
    public async Task ShouldProceedWithMaintenanceWhenEvictionCannotFreeEnough()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300, evictable: false);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);

        using var reservation = await manager.ReserveForMaintenanceAsync(
            store,
            StorageAdmissionKind.Compaction,
            5000,
            CancellationToken.None);

        Assert.Equal(5000, manager.Ledger.ReservedBytes);
    }

    [Fact]
    public async Task ShouldHydrateProtectedCompactionInputsWhenTheBudgetIsSaturated()
    {
        var store = new FakeHybridCacheStore(sstSizeBytes: 300);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        await manager.EnsureLocalSstsAsync(
            store,
            ["a.sst", "b.sst", "c.sst"],
            CancellationToken.None);
        using var protection = await manager.ProtectSstsFromEvictionAsync(
            ["a.sst", "b.sst", "c.sst", "d.sst"],
            CancellationToken.None);

        await manager.EnsureLocalSstsForMaintenanceAsync(
            store,
            ["d.sst"],
            CancellationToken.None);
        await manager.EvictIfNeededAsync(store, CancellationToken.None);

        Assert.Equal(["a.sst", "b.sst", "c.sst", "d.sst"], store.Hydrated);
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
