using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     SST names can be reused (for example by a retried, unpublished compaction output), so the
///     reader and block caches must be keyed by the manifest identity of a file rather than its
///     name: a warm cache may never serve a previous file's blocks for a new manifest entry, and a
///     scan already reading the previous file must finish from the handle it started with.
/// </summary>
public sealed class SstCacheManifestIdentityTests
{
    const int EntryCount = 256;
    const uint DefaultFamilyId = 0;
    const long BlockCacheBytes = 8L * 1024 * 1024;
    static readonly byte[] ProbeKey = KeyAt(EntryCount / 2);

    [Fact]
    public async Task ShouldReturnReplacedContentWhenSameNameHasNewManifestIdentityWithWarmCache()
    {
        using var original = new TemporaryDirectory();
        using var replacement = new TemporaryDirectory();
        var replacementFile = await PrepareReplacementAsync(original.Path, replacement.Path);
        using var store = OpenStore(original.Path);
        var originalFile = PointCandidates(store);

        WarmPointReads(store, originalFile);

        ReplaceSst(original.Path, replacement.Path, replacementFile);

        Assert.Equal("new", ValuePrefix(store.TryReadPointValue([replacementFile], ProbeKey)));
    }

    [Fact]
    public async Task ShouldReturnReplacedContentAsyncWhenSameNameHasNewManifestIdentityWithWarmCache()
    {
        using var original = new TemporaryDirectory();
        using var replacement = new TemporaryDirectory();
        var replacementFile = await PrepareReplacementAsync(original.Path, replacement.Path);
        using var store = OpenStore(original.Path);
        var originalFile = PointCandidates(store);

        await WarmPointReadsAsync(store, originalFile);

        ReplaceSst(original.Path, replacement.Path, replacementFile);

        Assert.Equal(
            "new",
            ValuePrefix(await store.TryReadPointValueAsync(
                [replacementFile],
                ProbeKey,
                CancellationToken.None)));
    }

    [Fact]
    public async Task ShouldFinishScanFromOriginalHandleWhenSstIsReplacedMidScan()
    {
        using var original = new TemporaryDirectory();
        using var replacement = new TemporaryDirectory();
        var replacementFile = await PrepareReplacementAsync(original.Path, replacement.Path);
        using var store = OpenStore(original.Path);
        var originalFile = PointCandidates(store);
        WarmPointReads(store, originalFile);
        var sources = store.CreateScanSources(originalFile, PantsScanDirection.Forward, null, null);
        try
        {
            var iterator = Assert.Single(sources).Iterator;
            Assert.True(iterator.MoveNext());
            var prefixes = new List<string> { ValuePrefix(iterator.Current) };

            ReplaceSst(original.Path, replacement.Path, replacementFile);
            Assert.Equal("new", ValuePrefix(store.TryReadPointValue([replacementFile], ProbeKey)));

            while (iterator.MoveNext())
            {
                prefixes.Add(ValuePrefix(iterator.Current));
            }

            Assert.Equal(EntryCount, prefixes.Count);
            Assert.All(prefixes, prefix => Assert.Equal("old", prefix));
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    [Fact]
    public async Task ShouldFinishAsyncScanFromOriginalHandleWhenSstIsReplacedMidScan()
    {
        using var original = new TemporaryDirectory();
        using var replacement = new TemporaryDirectory();
        var replacementFile = await PrepareReplacementAsync(original.Path, replacement.Path);
        using var store = OpenStore(original.Path);
        var originalFile = PointCandidates(store);
        await WarmPointReadsAsync(store, originalFile);
        var sources = await store.CreateScanSourcesAsync(
            originalFile,
            PantsScanDirection.Forward,
            null,
            null,
            null,
            CancellationToken.None);
        try
        {
            var source = Assert.Single(sources);
            Assert.True(await source.MoveNextAsync(CancellationToken.None));
            var prefixes = new List<string> { ValuePrefix(source.Current) };

            ReplaceSst(original.Path, replacement.Path, replacementFile);
            Assert.Equal(
                "new",
                ValuePrefix(await store.TryReadPointValueAsync(
                    [replacementFile],
                    ProbeKey,
                    CancellationToken.None)));

            while (await source.MoveNextAsync(CancellationToken.None))
            {
                prefixes.Add(ValuePrefix(source.Current));
            }

            Assert.Equal(EntryCount, prefixes.Count);
            Assert.All(prefixes, prefix => Assert.Equal("old", prefix));
        }
        finally
        {
            foreach (var source in sources)
            {
                await source.DisposeAsync();
            }
        }
    }

    /// <summary>
    ///     Builds the same flush in two databases so both publish an SST under the same name with
    ///     different contents and returns the replacement's manifest entry.
    /// </summary>
    static async Task<FileMeta> PrepareReplacementAsync(string originalRoot, string replacementRoot)
    {
        await WriteFlushedSstAsync(originalRoot, "old", 512);
        await WriteFlushedSstAsync(replacementRoot, "new", 640);
        using var store = OpenStore(replacementRoot);
        var replacementFile = Assert.Single(PointCandidates(store));
        using var originalStore = OpenStore(originalRoot);
        var originalFile = Assert.Single(PointCandidates(originalStore));
        Assert.Equal(originalFile.Name, replacementFile.Name);
        Assert.NotEqual(originalFile.SizeBytes, replacementFile.SizeBytes);
        return replacementFile;
    }

    static async Task WriteFlushedSstAsync(string root, string valuePrefix, int paddingLength)
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(root).WithBackgroundCompaction(false));
        var family = database.ColumnFamilies.DefaultFamily;
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < EntryCount; index++)
            {
                transaction.Put(
                    KeyAt(index),
                    TestBytes.FromString($"{valuePrefix}:{index:D4}:{new string('v', paddingLength)}"));
            }

            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.Maintenance.FlushAsync(family);
    }

    /// <summary>
    ///     Reads every key so the reader cache and each data block of the original SST are warm.
    /// </summary>
    static void WarmPointReads(LocalDiskStore store, IReadOnlyList<FileMeta> files)
    {
        for (var index = 0; index < EntryCount; index++)
        {
            Assert.Equal("old", ValuePrefix(store.TryReadPointValue(files, KeyAt(index))));
        }
    }

    static async Task WarmPointReadsAsync(LocalDiskStore store, IReadOnlyList<FileMeta> files)
    {
        for (var index = 0; index < EntryCount; index++)
        {
            Assert.Equal(
                "old",
                ValuePrefix(await store.TryReadPointValueAsync(
                    files,
                    KeyAt(index),
                    CancellationToken.None)));
        }
    }

    /// <summary>
    ///     Unlinks the original SST while the store still holds it open and publishes the
    ///     replacement's bytes under the same name, as a retried output would.
    /// </summary>
    static void ReplaceSst(string originalRoot, string replacementRoot, FileMeta file)
    {
        var target = FindSst(originalRoot, file.Name);
        File.Delete(target);
        File.Copy(FindSst(replacementRoot, file.Name), target);
    }

    static string FindSst(string root, string name) =>
        Assert.Single(Directory.GetFiles(root, name, SearchOption.AllDirectories));

    static LocalDiskStore OpenStore(string root) => LocalDiskStore.Open(
        root,
        new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()),
        blockCacheBytes: BlockCacheBytes);

    static IReadOnlyList<FileMeta> PointCandidates(LocalDiskStore store) =>
        store.GetSstReadView().SelectPointCandidates(DefaultFamilyId, ProbeKey, out _);

    static byte[] KeyAt(int index) => TestBytes.FromString($"key-{index:D4}");

    static string ValuePrefix(SstEntry? entry)
    {
        Assert.NotNull(entry);
        Assert.NotNull(entry.Value);
        return TestBytes.ToText(entry.Value).Split(':')[0];
    }
}
