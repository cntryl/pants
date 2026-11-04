using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsReadResourcesParityTests
{
    [Fact]
    public void ShouldRejectDefaultFourByteBudgetWithoutCompactionCapacity()
    {
        var error = Assert.Throws<PantsResourceLimitException>(() => PantsOpenOptionsResolver.Resolve(
            PantsOpenOptions.InMemory().WithMemoryBudget(PantsMemoryBudget.FromBytes(4))));

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldSplitTheResolvedReadAllowanceBetweenContentAndSharedMetadata(bool cloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (cloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "read-resources/")
                : PantsOpenOptions.Local(directory.Path))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithBackgroundCompaction(false);

        await using var database = await PantsDatabase.OpenAsync(options);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        var resolved = PantsOpenOptionsResolver.Resolve(options);

        Assert.Equal(1_101_006, metrics.BlockCacheCapacityBytes);
        Assert.Equal(1_468_007, resolved.ReadResourceBudgetBytes);
        Assert.Equal(367_001, resolved.ReadMetadataBudgetBytes);
        Assert.Equal(resolved.ReadMetadataBudgetBytes, metrics.ReadMetadataCapacityBytes);
        Assert.Equal(resolved.ReadResourceBudgetBytes, resolved.BlockCacheBytes + resolved.ReadMetadataBudgetBytes);
        Assert.Equal(838_860, resolved.TransactionMemoryPoolBytes);
        Assert.Equal(1_677_721, resolved.CompactionMemoryPoolBytes);
        Assert.Equal(2_202_010, resolved.MemtableSizeLimitBytes);
        Assert.Equal(0, resolved.ScanMemoryPoolBytes);
    }
    [Theory]
    [InlineData(PantsScanDirection.Forward)]
    [InlineData(PantsScanDirection.Reverse)]
    public async Task ShouldOpenOnlyReachedNonoverlappingFilesForAColdLimitedScan(PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1000, BackgroundEnabled: false));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 40; index++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                writer.Put(TestBytes.FromString($"key-{index:0000}"), TestBytes.FromString($"value-{index:0000}"));
                await writer.CommitAsync(PantsWriteOptions.BestEffort);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using (var scan = await reader.ScanAsync(new PantsScanQuery { Direction = direction, Limit = 1 }))
        {
            var rows = new List<PantsEntry>();
            await foreach (var row in scan)
            {
                rows.Add(row);
            }
            var first = direction == PantsScanDirection.Forward ? 0 : 39;
            Assert.Equal($"key-{first:0000}", TestBytes.ToText(Assert.Single(rows).Key));
            Assert.Equal($"value-{first:0000}", TestBytes.ToText(rows[0].Value));
        }
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.InRange(metrics.ReadMetadataUsedBytes, 1, 4096);
        Assert.InRange(metrics.ReadMetadataPeakBytes, 1, metrics.ReadMetadataCapacityBytes);
        Assert.Equal(40, metrics.SstCount);
    }
    [Fact]
    public async Task ShouldReadAndScanANearMaximumValueWithAReadAllowanceSmallerThanTheValue()
    {
        using var directory = new TemporaryDirectory();
        const int valueBytes = 64 * 1024 * 1024 - 256;
        var writeOptions = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
            .WithBackgroundCompaction(false);
        await using (var writerDatabase = await PantsDatabase.OpenAsync(writeOptions))
        {
            await using var writer = await writerDatabase.Transactions.BeginAsync(
                writerDatabase.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put("key"u8.ToArray(), Enumerable.Repeat((byte)'v', valueBytes).ToArray());
            await writer.CommitAsync(PantsWriteOptions.Sync);
            await writerDatabase.Maintenance.FlushAsync(writerDatabase.ColumnFamilies.DefaultFamily);
        }
        var readOptions = writeOptions.WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024));
        var resolved = PantsOpenOptionsResolver.Resolve(readOptions);
        Assert.True(resolved.ReadResourceBudgetBytes < valueBytes);
        await using var database = await PantsDatabase.OpenAsync(readOptions);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(valueBytes, (await reader.GetAsync("key"u8.ToArray()))!.Value.Length);
        foreach (var direction in new[] { PantsScanDirection.Forward, PantsScanDirection.Reverse })
        {
            foreach (var limit in new int?[] { 1, null })
            {
                await using var scan = await reader.ScanAsync(new PantsScanQuery { Direction = direction, Limit = limit });
                var count = 0;
                await foreach (var row in scan)
                {
                    Assert.Equal("key", TestBytes.ToText(row.Key));
                    Assert.Equal(valueBytes, row.Value.Length);
                    Assert.DoesNotContain(row.Value.ToArray(), static value => value != (byte)'v');
                    count++;
                }
                Assert.Equal(1, count);
            }
        }
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.InRange(metrics.ReadMetadataPeakBytes, 1, resolved.ReadMetadataBudgetBytes);
        Assert.Equal(0, metrics.ScanBufferCapacityBytes);
    }
    [Theory]
    [InlineData(64 * 1024, false)]
    [InlineData(96 * 1024, true)]
    [InlineData(128 * 1024, true)]
    public async Task ShouldMatchNativeSmallBudgetOpenAdmissionAndPreserveRowsAfterRefusal(int budgetBytes, bool admitted)
    {
        using var directory = new TemporaryDirectory();
        var seedOptions = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(128 * 1024))
            .WithBackgroundCompaction(false);
        await using (var seedDatabase = await PantsDatabase.OpenAsync(seedOptions))
        {
            await using var writer = await seedDatabase.Transactions.BeginAsync(
                seedDatabase.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put("key"u8.ToArray(), "v"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Sync);
            await seedDatabase.Maintenance.FlushAsync(seedDatabase.ColumnFamilies.DefaultFamily);
        }
        await using (var database = await PantsDatabase.OpenAsync(seedOptions.WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes))))
        {
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ReadMetadataUsedBytes);
            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            if (admitted)
            {
                Assert.Equal("v", TestBytes.ToText((await reader.GetAsync("key"u8.ToArray()))!.Value));
            }
            else
            {
                var error = await Assert.ThrowsAsync<PantsResourceLimitException>(() => reader.GetAsync("key"u8.ToArray()).AsTask());
                Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
            }
            async Task ReadScanAsync()
            {
                await using var scan = await reader.ScanAsync(new PantsScanQuery { Limit = 1 });
                var count = 0;
                await foreach (var row in scan)
                {
                    Assert.Equal("key", TestBytes.ToText(row.Key));
                    Assert.Equal("v", TestBytes.ToText(row.Value));
                    count++;
                }
                Assert.Equal(1, count);
            }
            if (admitted)
            {
                await ReadScanAsync();
            }
            else
            {
                await Assert.ThrowsAsync<PantsResourceLimitException>(ReadScanAsync);
            }
        }
        await using var recovered = await PantsDatabase.OpenAsync(seedOptions);
        await using var recoveredReader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("v", TestBytes.ToText((await recoveredReader.GetAsync("key"u8.ToArray()))!.Value));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveAcknowledgedRowsAfterTinyMaintenanceRefusalAndReopen(bool cloud)
    {
        using var directory = new TemporaryDirectory();
        var baseOptions = cloud
            ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "tiny-maintenance/")
            : PantsOpenOptions.Local(directory.Path);
        var options = baseOptions.WithMemoryBudget(PantsMemoryBudget.FromBytes(cloud ? 512 * 1024 : 64 * 1024));
        var acknowledged = new List<string>();
        PantsResourceLimitException? failure = null;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 64 && failure is null; index++)
            {
                var key = $"key-{index:000}";
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                writer.Put(TestBytes.FromString(key), new byte[1024]);
                // Overlapping range extents force physical work rather than a metadata-only move.
                writer.DeleteRange("z-start"u8.ToArray(), "zz-end"u8.ToArray());
                await writer.CommitAsync(cloud ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Sync);
                acknowledged.Add(key);
                if (index % 10 == 0)
                {
                    try
                    {
                        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
                    }
                    catch (PantsResourceLimitException error)
                    {
                        failure = error;
                    }
                }
            }
            Assert.NotEmpty(acknowledged);
            Assert.NotNull(failure);
            Assert.Equal(PantsErrorCode.ResourceLimit, failure.Code);
        }
        await using var reopened = await PantsDatabase.OpenAsync(
            baseOptions.WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024 * 1024)));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var keys = new List<string>();
        await foreach (var row in scan)
        {
            Assert.Equal(1024, row.Value.Length);
            Assert.DoesNotContain(row.Value.ToArray(), static value => value != 0);
            keys.Add(TestBytes.ToText(row.Key));
        }
        Assert.Equal(acknowledged, keys);
        await using var retry = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        retry.Put("after-pressure"u8.ToArray(), new byte[] { 1 });
        await retry.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }
}
