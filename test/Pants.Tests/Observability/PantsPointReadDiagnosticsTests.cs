using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Cntryl.Pants.Tests;

public sealed class PantsPointReadDiagnosticsTests
{
    const long HybridLocalBudgetBytes = 128 * 1024;

    [Fact]
    public void ShouldExposeImmutableSstTraceThroughReadonlyCollectionContract()
    {
        var expected = new PantsSstReadTrace(
            "expected.sst",
            0,
            PantsSstReadTier.Local,
            PantsBloomFilterOutcome.TruePositive,
            PantsCacheReadOutcome.Miss,
            PantsCacheReadOutcome.Miss,
            1);
        var replacement = expected with { Name = "replacement.sst" };
        var source = new List<PantsSstReadTrace> { expected };

        var trace = new PantsPointReadTrace(0, source);
        source[0] = replacement;
        source.Add(replacement);

        var property = typeof(PantsPointReadTrace).GetProperty(nameof(PantsPointReadTrace.Ssts));
        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyList<PantsSstReadTrace>), property.PropertyType);
        Assert.IsType<ImmutableArray<PantsSstReadTrace>>(trace.Ssts);
        Assert.Same(expected, Assert.Single(trace.Ssts));
    }

    [Fact]
    public async Task ShouldReturnEmptySstTraceGivenInMemoryPointRead()
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory());
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var result = await reader.GetWithDiagnosticsAsync("key"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(
            Assert.IsType<ReadOnlyMemory<byte>>(result.Value)));
        Assert.Equal(0, result.Trace.KeyRangeRejects);
        Assert.Empty(result.Trace.Ssts);
    }

    [Fact]
    public async Task ShouldKeepLocalPointReadTracesQueryScopedGivenCachesWarmBetweenReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await FlushAsync(database, "alpha", "first"u8.ToArray());
        await FlushAsync(database, "alpha", "second"u8.ToArray());
        await FlushAsync(database, "zulu", "unrelated"u8.ToArray());
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var cold = await reader.GetWithDiagnosticsAsync("alpha"u8.ToArray());
        var warm = await reader.GetWithDiagnosticsAsync("alpha"u8.ToArray());

        Assert.Equal("second", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(cold.Value)));
        Assert.Equal(1, cold.Trace.KeyRangeRejects);
        Assert.Equal(2, cold.Trace.Ssts.Count);
        Assert.Equal(2, cold.Trace.Ssts.Select(static sst => sst.Name).Distinct().Count());
        Assert.All(cold.Trace.Ssts, static sst =>
        {
            Assert.EndsWith(".sst", sst.Name, StringComparison.Ordinal);
            Assert.Equal(0U, sst.Level);
            Assert.Equal(PantsSstReadTier.Local, sst.Tier);
            Assert.Equal(PantsBloomFilterOutcome.TruePositive, sst.BloomFilterOutcome);
            Assert.Equal(PantsCacheReadOutcome.Miss, sst.ReaderCacheOutcome);
            Assert.Equal(PantsCacheReadOutcome.Miss, sst.BlockCacheOutcome);
            Assert.Equal(1, sst.DataBlocksRead);
        });

        Assert.Equal("second", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(warm.Value)));
        Assert.Equal(1, warm.Trace.KeyRangeRejects);
        Assert.Equal(
            cold.Trace.Ssts.Select(static sst => sst.Name).Order(StringComparer.Ordinal),
            warm.Trace.Ssts.Select(static sst => sst.Name).Order(StringComparer.Ordinal));
        Assert.All(warm.Trace.Ssts, static sst =>
        {
            Assert.Equal(PantsSstReadTier.Local, sst.Tier);
            Assert.Equal(PantsBloomFilterOutcome.TruePositive, sst.BloomFilterOutcome);
            Assert.Equal(PantsCacheReadOutcome.Hit, sst.ReaderCacheOutcome);
            Assert.Equal(PantsCacheReadOutcome.Hit, sst.BlockCacheOutcome);
            Assert.Equal(0, sst.DataBlocksRead);
        });

        Assert.All(cold.Trace.Ssts, static sst =>
        {
            Assert.Equal(PantsCacheReadOutcome.Miss, sst.BlockCacheOutcome);
            Assert.Equal(1, sst.DataBlocksRead);
        });
        var aggregate = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(2, aggregate.SstReaderCacheMisses);
        Assert.Equal(2, aggregate.SstReaderCacheHits);
        Assert.Equal(2, aggregate.SstBlockCacheMisses);
        Assert.Equal(2, aggregate.SstBlockCacheHits);
        Assert.Equal(2, aggregate.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldExplainCloudHydrationOnlyForQueryThatFetchedEvictedSst()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "diagnostics/")
                .WithSimulatedCloudLocalStorageBudget(HybridLocalBudgetBytes)
                .WithBackgroundCompaction(false));
        var expected = CreateValue(256 * 1024, seed: 83);
        await FlushAsync(database, "cloud-key", expected, PantsWriteOptions.CloudStrict);
        Assert.Empty(LocalSsts(directory.Path));
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var hydrated = await reader.GetWithDiagnosticsAsync("cloud-key"u8.ToArray());
        var resident = await reader.GetWithDiagnosticsAsync("cloud-key"u8.ToArray());

        Assert.Equal(expected, Assert.IsType<ReadOnlyMemory<byte>>(hydrated.Value).ToArray());
        Assert.Equal(0, hydrated.Trace.KeyRangeRejects);
        var hydratedSst = Assert.Single(hydrated.Trace.Ssts);
        Assert.Equal(PantsSstReadTier.HydratedFromCloud, hydratedSst.Tier);
        Assert.Equal(PantsBloomFilterOutcome.TruePositive, hydratedSst.BloomFilterOutcome);
        Assert.Equal(PantsCacheReadOutcome.Miss, hydratedSst.ReaderCacheOutcome);
        Assert.Equal(PantsCacheReadOutcome.Miss, hydratedSst.BlockCacheOutcome);
        Assert.Equal(1, hydratedSst.DataBlocksRead);

        Assert.Equal(expected, Assert.IsType<ReadOnlyMemory<byte>>(resident.Value).ToArray());
        Assert.Equal(0, resident.Trace.KeyRangeRejects);
        var residentSst = Assert.Single(resident.Trace.Ssts);
        Assert.Equal(hydratedSst.Name, residentSst.Name);
        Assert.Equal(PantsSstReadTier.Local, residentSst.Tier);
        Assert.Equal(PantsCacheReadOutcome.Hit, residentSst.ReaderCacheOutcome);
        Assert.Equal(PantsCacheReadOutcome.Hit, residentSst.BlockCacheOutcome);
        Assert.Equal(0, residentSst.DataBlocksRead);
        Assert.Single(LocalSsts(directory.Path));

        Assert.Equal(PantsSstReadTier.HydratedFromCloud, hydratedSst.Tier);
        Assert.Equal(1, hydratedSst.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldCopyPointReadInputAndOutputGivenDiagnosticsRequested()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await FlushAsync(database, "owned-key", "stable-value"u8.ToArray());
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var key = "owned-key"u8.ToArray();

        var pendingRead = reader.GetWithDiagnosticsAsync(key);
        key.AsSpan().Clear();
        var first = await pendingRead;
        var firstValue = Assert.IsType<ReadOnlyMemory<byte>>(first.Value);
        Assert.True(MemoryMarshal.TryGetArray(firstValue, out var exposed));
        exposed.Array![exposed.Offset] = (byte)'X';

        var second = await reader.GetWithDiagnosticsAsync("owned-key"u8.ToArray());

        Assert.Equal("stable-value", TestBytes.ToText(
            Assert.IsType<ReadOnlyMemory<byte>>(second.Value)));
        Assert.Single(first.Trace.Ssts);
        Assert.Single(second.Trace.Ssts);
    }

    [Fact]
    public async Task ShouldExplainBloomRejectionWithoutReportingDataBlockRead()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("alpha"u8.ToArray(), "first"u8.ToArray());
            writer.Put("zulu"u8.ToArray(), "last"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var result = await reader.GetWithDiagnosticsAsync("middle"u8.ToArray());

        Assert.Null(result.Value);
        Assert.Equal(0, result.Trace.KeyRangeRejects);
        var sst = Assert.Single(result.Trace.Ssts);
        Assert.Equal(PantsBloomFilterOutcome.Rejected, sst.BloomFilterOutcome);
        Assert.Equal(PantsCacheReadOutcome.Miss, sst.ReaderCacheOutcome);
        Assert.Equal(PantsCacheReadOutcome.NotChecked, sst.BlockCacheOutcome);
        Assert.Equal(0, sst.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldSkipSstsGivenPointReadResolvedByTransactionIntent()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("put-key"u8.ToArray(), "persisted-put"u8.ToArray());
            writer.Put("delete-key"u8.ToArray(), "persisted-delete"u8.ToArray());
            writer.Put("range-mid"u8.ToArray(), "persisted-range"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("put-key"u8.ToArray(), "staged-put"u8.ToArray());
        transaction.Delete("delete-key"u8.ToArray());
        transaction.DeleteRange("range-a"u8.ToArray(), "range-z"u8.ToArray());
        var before = await database.GetReadPathDiagnosticsAsync();
        var amplificationBefore = await database.GetReadAmplificationMetricsAsync();

        var put = await transaction.GetWithDiagnosticsAsync("put-key"u8.ToArray());
        var deleted = await transaction.GetWithDiagnosticsAsync("delete-key"u8.ToArray());
        var rangeDeleted = await transaction.GetWithDiagnosticsAsync("range-mid"u8.ToArray());
        var ordinaryPut = await transaction.GetAsync("put-key"u8.ToArray());
        var ordinaryDeleted = await transaction.GetAsync("delete-key"u8.ToArray());
        var ordinaryRangeDeleted = await transaction.GetAsync("range-mid"u8.ToArray());

        Assert.Equal("staged-put", TestBytes.ToText(
            Assert.IsType<ReadOnlyMemory<byte>>(put.Value)));
        Assert.Null(deleted.Value);
        Assert.Null(rangeDeleted.Value);
        Assert.All(
            new[] { put.Trace, deleted.Trace, rangeDeleted.Trace },
            static trace =>
            {
                Assert.Equal(0, trace.KeyRangeRejects);
                Assert.Empty(trace.Ssts);
            });
        Assert.Equal("staged-put", TestBytes.ToText(
            Assert.IsType<ReadOnlyMemory<byte>>(ordinaryPut)));
        Assert.Null(ordinaryDeleted);
        Assert.Null(ordinaryRangeDeleted);
        Assert.Equal(before, await database.GetReadPathDiagnosticsAsync());
        Assert.Equal(amplificationBefore, await database.GetReadAmplificationMetricsAsync());
    }

    static async ValueTask FlushAsync(
        IPantsDatabase database,
        string key,
        ReadOnlyMemory<byte> value,
        PantsWriteOptions? options = null)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(options ?? PantsWriteOptions.Buffered);
        await database.FlushAsync(database.DefaultColumnFamily);
    }

    static byte[] CreateValue(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    static string[] LocalSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "sst"), "*.sst");
}
