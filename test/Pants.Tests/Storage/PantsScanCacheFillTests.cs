using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsScanCacheFillTests
{
    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 64, true)]
    [InlineData(false, 65, false)]
    [InlineData(false, null, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 64, true)]
    [InlineData(true, 65, false)]
    [InlineData(true, null, false)]
    public Task ShouldWarmPointReadsOnlyForSmallLimitedScansByDefault(
        bool simulatedCloud,
        int? limit,
        bool fillsCache) =>
        AssertPolicyAsync(simulatedCloud, PantsScanDirection.Forward, limit, null, fillsCache);

    [Theory]
    [InlineData(false, PantsScanDirection.Forward, 1, PantsCacheFill.Bypass, false)]
    [InlineData(false, PantsScanDirection.Reverse, 1, PantsCacheFill.Bypass, false)]
    [InlineData(false, PantsScanDirection.Forward, 65, PantsCacheFill.Normal, true)]
    [InlineData(false, PantsScanDirection.Reverse, 65, PantsCacheFill.Normal, true)]
    [InlineData(false, PantsScanDirection.Forward, null, PantsCacheFill.Normal, true)]
    [InlineData(false, PantsScanDirection.Reverse, null, PantsCacheFill.Normal, true)]
    [InlineData(true, PantsScanDirection.Forward, 1, PantsCacheFill.Bypass, false)]
    [InlineData(true, PantsScanDirection.Reverse, 1, PantsCacheFill.Bypass, false)]
    [InlineData(true, PantsScanDirection.Forward, 65, PantsCacheFill.Normal, true)]
    [InlineData(true, PantsScanDirection.Reverse, 65, PantsCacheFill.Normal, true)]
    [InlineData(true, PantsScanDirection.Forward, null, PantsCacheFill.Normal, true)]
    [InlineData(true, PantsScanDirection.Reverse, null, PantsCacheFill.Normal, true)]
    public Task ShouldHonorExplicitFillPolicyInEitherDirection(
        bool simulatedCloud,
        PantsScanDirection direction,
        int? limit,
        PantsCacheFill cacheFill,
        bool fillsCache) =>
        AssertPolicyAsync(simulatedCloud, direction, limit, cacheFill, fillsCache);

    [Theory]
    [InlineData(PantsScanDirection.Forward, 10, null, true)]
    [InlineData(PantsScanDirection.Reverse, 10, null, true)]
    [InlineData(PantsScanDirection.Forward, 1, PantsCacheFill.Bypass, false)]
    [InlineData(PantsScanDirection.Reverse, 1, PantsCacheFill.Bypass, false)]
    [InlineData(PantsScanDirection.Forward, null, PantsCacheFill.Normal, true)]
    [InlineData(PantsScanDirection.Reverse, null, PantsCacheFill.Normal, true)]
    public async Task ShouldApplyFillPolicyToActualProviderRangedScans(
        PantsScanDirection direction,
        int? limit,
        PantsCacheFill? cacheFill,
        bool fillsCache)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "scan-policy");
        var options = PantsOpenOptions.Cloud(directory.Path, location)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024 * 1024))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        var expectedValue = await SeedAsync(database);
        var sst = Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        File.Delete(sst);
        var before = handler.SstRangeReads;
        var fullReads = handler.SstFullReads;

        await AssertScanAndPointAsync(database, direction, limit, cacheFill, fillsCache, expectedValue);

        Assert.True(handler.SstRangeReads > before);
        Assert.Equal(fullReads, handler.SstFullReads);
        Assert.False(File.Exists(sst));
    }

    [Fact]
    public async Task ShouldRejectAnInvalidFillPolicyWithoutRegisteringAScan()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var before = await database.Diagnostics.GetReadPathDiagnosticsAsync();

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.ScanAsync(new PantsScanQuery { CacheFill = (PantsCacheFill)17 }).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        var after = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        Assert.Equal(before.SnapshotsRegistered, after.SnapshotsRegistered);
        Assert.Equal(before.SnapshotsUnregistered, after.SnapshotsUnregistered);
        Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
    }

    [Theory]
    [InlineData(false, PantsScanDirection.Forward)]
    [InlineData(false, PantsScanDirection.Reverse)]
    [InlineData(true, PantsScanDirection.Forward)]
    [InlineData(true, PantsScanDirection.Reverse)]
    public async Task ShouldReuseCachedBlocksWhileBypassingInsertionOfOtherBlocks(
        bool simulatedCloud,
        PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        var options = (simulatedCloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "scan-cache", "cached-bypass")
                : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024 * 1024))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var expectedValue = await SeedAsync(database);
        var firstKey = direction == PantsScanDirection.Forward ? "key-0000" : "key-0127";
        await using (var warm = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(expectedValue, Assert.IsType<ReadOnlyMemory<byte>>(
                await warm.GetAsync(TestBytes.FromString(firstKey))).ToArray());
        }

        var before = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        var cacheBytes = (await database.Diagnostics.GetRuntimeMetricsAsync()).BlockCacheUsedBytes;
        Assert.True(cacheBytes > 0);

        await AssertScanAndPointAsync(database, direction, null, PantsCacheFill.Bypass, true, expectedValue);

        var after = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        Assert.True(after.SstBlockCacheHits >= before.SstBlockCacheHits + 2);
        Assert.True(after.SstBlockCacheMisses > before.SstBlockCacheMisses);
        Assert.Equal(cacheBytes, (await database.Diagnostics.GetRuntimeMetricsAsync()).BlockCacheUsedBytes);
    }

    static async Task AssertPolicyAsync(
        bool simulatedCloud,
        PantsScanDirection direction,
        int? limit,
        PantsCacheFill? cacheFill,
        bool fillsCache)
    {
        using var directory = new TemporaryDirectory();
        var options = (simulatedCloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "scan-cache", "default-policy")
                : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024 * 1024))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var expectedValue = await SeedAsync(database);
        await AssertScanAndPointAsync(database, direction, limit, cacheFill, fillsCache, expectedValue);
    }

    static async Task<byte[]> SeedAsync(IPantsDatabase database)
    {
        var expectedValue = Enumerable.Repeat((byte)0x5d, 1024).ToArray();
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 128; index++)
            {
                writer.Put(TestBytes.FromString($"key-{index:D4}"), expectedValue);
            }

            await writer.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
        return expectedValue;
    }

    static async Task AssertScanAndPointAsync(
        IPantsDatabase database,
        PantsScanDirection direction,
        int? limit,
        PantsCacheFill? cacheFill,
        bool fillsCache,
        byte[] expectedValue)
    {
        await using (var reader = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        await using (var scan = await reader.ScanAsync(new PantsScanQuery
        {
            Limit = limit,
            Direction = direction,
            CacheFill = cacheFill
        }))
        {
            var count = 0;
            await foreach (var row in scan)
            {
                var index = direction == PantsScanDirection.Forward ? count : 127 - count;
                Assert.Equal($"key-{index:D4}", TestBytes.ToText(row.Key));
                Assert.Equal(expectedValue, row.Value.ToArray());
                count++;
            }

            Assert.Equal(limit ?? 128, count);
        }

        var before = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        await using var pointReader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var firstKey = direction == PantsScanDirection.Forward ? "key-0000" : "key-0127";
        var value = await pointReader.GetAsync(TestBytes.FromString(firstKey));
        Assert.Equal(expectedValue, Assert.IsType<ReadOnlyMemory<byte>>(value).ToArray());
        var after = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        Assert.Equal(before.SstBlockCacheMisses + (fillsCache ? 0 : 1), after.SstBlockCacheMisses);
        Assert.Equal(before.SstBlockCacheHits + (fillsCache ? 1 : 0), after.SstBlockCacheHits);
    }
}
