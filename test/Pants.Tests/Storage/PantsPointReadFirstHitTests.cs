using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     A point read walks its SST sources newest-first and stops at the first group that decides
///     the key: one L0 file whose sequence range is strictly ordered against its neighbours, or
///     one whole deeper level. Files that cannot be ordered against each other are read together.
/// </summary>
public sealed class PantsPointReadFirstHitTests
{
    const long HybridLocalBudgetBytes = 8L * 1024 * 1024;
    const uint DefaultFamilyId = 0;
    static readonly byte[] Key = "alpha"u8.ToArray();

    public enum StorageMode
    {
        Local,
        Hybrid
    }

    [Theory]
    [InlineData(StorageMode.Local)]
    [InlineData(StorageMode.Hybrid)]
    public async Task ShouldStopAtNewestSstGivenOlderFlushHoldsStaleVersion(StorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path));
        await PutAndFlushAsync(database, mode, "first");
        await PutAndFlushAsync(database, mode, "second");
        var newest = await NewestLevelZeroFileAsync(database);
        var before = await database.Diagnostics.GetReadAmplificationMetricsAsync();

        var result = await ReadWithDiagnosticsAsync(database);

        Assert.Equal("second", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(result.Value)));
        var sst = Assert.Single(result.Trace.Ssts);
        Assert.Equal(newest.Name, sst.Name);
        Assert.Equal(PantsBloomFilterOutcome.TruePositive, sst.BloomFilterOutcome);
        var after = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(1, after.SstsTouchedTotal - before.SstsTouchedTotal);
    }

    [Theory]
    [InlineData(StorageMode.Local)]
    [InlineData(StorageMode.Hybrid)]
    public async Task ShouldStopAtNewestSstAfterReopenGivenOlderFlushHoldsStaleVersion(StorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        string newestName;
        await using (var database = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path)))
        {
            await PutAndFlushAsync(database, mode, "first");
            await PutAndFlushAsync(database, mode, "second");
            newestName = (await NewestLevelZeroFileAsync(database)).Name;
        }

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path));

        var result = await ReadWithDiagnosticsAsync(reopened);

        Assert.Equal("second", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(result.Value)));
        Assert.Equal(newestName, Assert.Single(result.Trace.Ssts).Name);
    }

    [Theory]
    [InlineData(StorageMode.Local)]
    [InlineData(StorageMode.Hybrid)]
    public async Task ShouldStopAtPointTombstoneGivenOlderFlushHoldsDeletedValue(StorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path));
        await PutAndFlushAsync(database, mode, "first");
        await MutateAndFlushAsync(database, mode, static transaction => transaction.Delete(Key));
        var newest = await NewestLevelZeroFileAsync(database);

        var result = await ReadWithDiagnosticsAsync(database);

        Assert.Null(result.Value);
        Assert.Equal(newest.Name, Assert.Single(result.Trace.Ssts).Name);
    }

    [Theory]
    [InlineData(StorageMode.Local)]
    [InlineData(StorageMode.Hybrid)]
    public async Task ShouldStopAtCoveringRangeTombstoneGivenOlderFlushHoldsDeletedValue(StorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(mode, directory.Path));
        await PutAndFlushAsync(database, mode, "first");
        await MutateAndFlushAsync(
            database,
            mode,
            static transaction => transaction.DeleteRange("a"u8.ToArray(), "b"u8.ToArray()));
        var newest = await NewestLevelZeroFileAsync(database);

        var result = await ReadWithDiagnosticsAsync(database);

        Assert.Null(result.Value);
        Assert.Equal(newest.Name, Assert.Single(result.Trace.Ssts).Name);
    }

    [Fact]
    public async Task ShouldStopAtLevelZeroGivenDeeperLevelHoldsStaleVersion()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateOptions(StorageMode.Local, directory.Path));
        await PutAndFlushAsync(database, StorageMode.Local, "first");
        await PutAndFlushAsync(database, StorageMode.Local, "second");
        await database.Maintenance.CompactAllAsync();
        await PutAndFlushAsync(database, StorageMode.Local, "third");
        var files = await FamilyFilesAsync(database);
        var levelZero = Assert.Single(files, static file => file.Level == 0);
        Assert.Contains(files, static file => file.Level > 0);

        var result = await ReadWithDiagnosticsAsync(database);

        Assert.Equal("third", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(result.Value)));
        var sst = Assert.Single(result.Trace.Ssts);
        Assert.Equal(levelZero.Name, sst.Name);
        Assert.Equal(0U, sst.Level);
    }

    [Fact]
    public async Task ShouldFallThroughToOlderSstGivenNewerSstDoesNotHoldTheKey()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateOptions(StorageMode.Local, directory.Path));
        await PutAndFlushAsync(database, StorageMode.Local, "first");
        await MutateAndFlushAsync(database, StorageMode.Local, static transaction =>
        {
            transaction.Put("aardvark"u8.ToArray(), "low"u8.ToArray());
            transaction.Put("zulu"u8.ToArray(), "high"u8.ToArray());
        });

        var result = await ReadWithDiagnosticsAsync(database);

        Assert.Equal("first", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(result.Value)));
        Assert.Equal(2, result.Trace.Ssts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReturnNewerVersionGivenLevelZeroFilesCannotBeOrderedBySequence(
        bool sequenceRangeMissing)
    {
        using var directory = new TemporaryDirectory();
        await WriteTwoFlushesAsync(directory.Path);
        using var store = OpenStore(directory.Path);
        var candidates = UnorderedCandidatesStaleFirst(store, sequenceRangeMissing);

        var entry = store.TryReadPointValue(candidates, Key);

        Assert.NotNull(entry);
        Assert.Equal("second", TestBytes.ToText(entry.Value!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReturnNewerVersionAsyncGivenLevelZeroFilesCannotBeOrderedBySequence(
        bool sequenceRangeMissing)
    {
        using var directory = new TemporaryDirectory();
        await WriteTwoFlushesAsync(directory.Path);
        using var store = OpenStore(directory.Path);
        var candidates = UnorderedCandidatesStaleFirst(store, sequenceRangeMissing);

        var entry = await store.TryReadPointValueAsync(candidates, Key, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal("second", TestBytes.ToText(entry.Value!));
    }

    /// <summary>
    ///     Presents the two flushed files the way a manifest without usable sequence ordering
    ///     would: the file holding the stale version sorts first, and the recorded sequence ranges
    ///     either overlap or are absent, so neither file may decide the key on its own.
    /// </summary>
    static FileMeta[] UnorderedCandidatesStaleFirst(LocalDiskStore store, bool sequenceRangeMissing)
    {
        var published = store.GetSstReadView().SelectPointCandidates(DefaultFamilyId, Key, out _);
        Assert.Equal(2, published.Count);
        var newer = published[0];
        var older = published[1];
        Assert.True(older.LargestSequence < newer.SmallestSequence);

        var staleFirst = older.Clone();
        var freshSecond = newer.Clone();
        if (sequenceRangeMissing)
        {
            freshSecond.SmallestSequence = null;
            freshSecond.LargestSequence = null;
        }
        else
        {
            staleFirst.SmallestSequence = older.SmallestSequence;
            staleFirst.LargestSequence = newer.LargestSequence;
            freshSecond.SmallestSequence = older.SmallestSequence;
            freshSecond.LargestSequence = newer.LargestSequence;
        }

        return [staleFirst, freshSecond];
    }

    static async Task WriteTwoFlushesAsync(string root)
    {
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(StorageMode.Local, root));
        await PutAndFlushAsync(database, StorageMode.Local, "first");
        await PutAndFlushAsync(database, StorageMode.Local, "second");
    }

    static LocalDiskStore OpenStore(string root) => LocalDiskStore.Open(
        root,
        new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()),
        blockCacheBytes: 8L * 1024 * 1024);

    static PantsOpenOptions CreateOptions(StorageMode mode, string root) => mode switch
    {
        StorageMode.Local => PantsOpenOptions.Local(root).WithBackgroundCompaction(false),
        StorageMode.Hybrid => PantsOpenOptions.SimulatedCloud(root, "pants-tests", "point-read-first-hit/")
            .WithSimulatedCloudLocalStorageBudget(HybridLocalBudgetBytes)
            .WithBackgroundCompaction(false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    static ValueTask PutAndFlushAsync(IPantsDatabase database, StorageMode mode, string value) =>
        MutateAndFlushAsync(database, mode, transaction => transaction.Put(Key, TestBytes.FromString(value)));

    static async ValueTask MutateAndFlushAsync(
        IPantsDatabase database,
        StorageMode mode,
        Action<IPantsTransaction> mutate)
    {
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            mutate(transaction);
            await transaction.CommitAsync(
                mode == StorageMode.Hybrid ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async ValueTask<PantsPointReadResult> ReadWithDiagnosticsAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await reader.GetWithDiagnosticsAsync(Key);
    }

    static async ValueTask<PantsStorageFileLayout[]> FamilyFilesAsync(IPantsDatabase database)
    {
        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        return [.. layout.Levels
            .SelectMany(static level => level.Files)
            .Where(file => file.ColumnFamilyId == database.ColumnFamilies.DefaultFamily.Id)];
    }

    /// <summary>The level-zero file with the highest recorded sequence range.</summary>
    static async ValueTask<PantsStorageFileLayout> NewestLevelZeroFileAsync(IPantsDatabase database)
    {
        var levelZero = (await FamilyFilesAsync(database)).Where(static file => file.Level == 0).ToArray();
        Assert.Equal(2, levelZero.Length);
        Assert.All(levelZero, static file =>
        {
            Assert.NotNull(file.SmallestSequence);
            Assert.NotNull(file.LargestSequence);
        });
        var ordered = levelZero.OrderBy(static file => file.LargestSequence).ToArray();
        Assert.True(ordered[0].LargestSequence < ordered[1].SmallestSequence);
        return ordered[1];
    }
}
