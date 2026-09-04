using System.Text;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

/// <summary>
///     Slice 6c (issue #219): runtime metrics distinguish active vs. immutable memtable bytes and
///     expose block-cache used bytes, rather than only a single combined
///     <see cref="PantsRuntimeMetrics.TotalMemtableBytes" /> and hit/miss counters. Also covers the
///     exact acceptance-criteria wording: "TotalMemtableBytes never excludes live in-memory records
///     merely because their duplicate SST was published" — i.e. a flush must not zero out
///     <see cref="PantsRuntimeMetrics.ActiveMemtableBytes" /> until the write that triggered it is
///     actually durable and the generation is released, not merely because a publish happened.
/// </summary>
public sealed class PantsMemtableAndCacheMetricsContractTests
{
    [Fact]
    public async Task ShouldReportActiveMemtableBytesSeparatelyFromImmutableMemtableBytes()
    {
        using var directory = new TemporaryDirectory();
        var handler = new BlockingFlushBuildFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(handler));
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), new byte[1024]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        var beforeFlush = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(beforeFlush.ActiveMemtableBytes > 0);
        Assert.Equal(0, beforeFlush.ImmutableMemtableBytes);
        Assert.Equal(
            beforeFlush.ActiveMemtableBytes + beforeFlush.ImmutableMemtableBytes,
            beforeFlush.TotalMemtableBytes);

        // Freeze the memtable and start the flush, but hold it at BeforeFlushBuild — the frozen
        // generation is already tracked as immutable (RuntimeState.ImmutableMemtableFlushes) at
        // this point, and stays there until the flush completes, so this actually observes an
        // in-flight immutable generation rather than sampling after it was already released.
        var flushTask = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
        await handler.WaitUntilBlockedAsync(TimeSpan.FromSeconds(10));

        var duringFlush = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, duringFlush.ActiveMemtableBytes);
        Assert.True(duringFlush.ImmutableMemtableBytes > 0);
        Assert.Equal(
            duringFlush.ActiveMemtableBytes + duringFlush.ImmutableMemtableBytes,
            duringFlush.TotalMemtableBytes);

        handler.Release();
        await flushTask;

        var afterFlush = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, afterFlush.ActiveMemtableBytes);
        Assert.Equal(0, afterFlush.ImmutableMemtableBytes);
        Assert.Equal(0, afterFlush.TotalMemtableBytes);
    }

    [Fact]
    public async Task ShouldReportBlockCacheUsedBytesGrowingAfterARealSstBlockRead()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), new byte[1024]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        _ = await reader.GetAsync("key"u8.ToArray());

        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(after.BlockCacheUsedBytes > before.BlockCacheUsedBytes);
        Assert.True(after.BlockCacheCapacityBytes > 0);
    }

    [Fact]
    public async Task ShouldRefreshCacheAndCompactionBufferBytesWhileCompactionIsLive()
    {
        using var directory = new TemporaryDirectory();
        var handler = new BlockingCompactionOutputFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(handler));
        for (var generation = 0; generation < 2; generation++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 100; index++)
                {
                    transaction.Put(
                        Encoding.UTF8.GetBytes($"key-{generation:D2}-{index:D4}"),
                        new byte[1024]);
                }

                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var compactTask = database.Maintenance.CompactAllAsync().AsTask();
        await handler.WaitUntilBlockedAsync(TimeSpan.FromSeconds(10));
        try
        {
            var beforeRead = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(beforeRead.CompactionBufferUsedBytes > 0);
            Assert.True(beforeRead.CompactionBufferPeakBytes >= beforeRead.CompactionBufferUsedBytes);
            Assert.Equal(
                RuntimePlan.Resolve(options).CompactionMemoryPoolBytes,
                beforeRead.CompactionBufferCapacityBytes);

            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var value = await reader.GetAsync("key-00-0000"u8.ToArray());
            Assert.NotNull(value);

            var afterRead = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(afterRead.BlockCacheUsedBytes > beforeRead.BlockCacheUsedBytes);
        }
        finally
        {
            handler.Release();
        }

        await compactTask;
        var afterCompaction = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, afterCompaction.CompactionBufferUsedBytes);
        Assert.True(afterCompaction.CompactionBufferPeakBytes > 0);
    }

    [Fact]
    public async Task ShouldReportAndReleaseScanBufferBytesForAnActiveSstScan()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                transaction.Put(
                    Encoding.UTF8.GetBytes($"key-{index:D4}"),
                    new byte[1024]);
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        await using (var scan = await transaction.ScanAsync(new PantsScanQuery()))
        {
            var enumerator = scan.GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());

            var duringScan = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(duringScan.ScanBufferUsedBytes > 0);
            Assert.True(duringScan.ScanBufferPeakBytes >= duringScan.ScanBufferUsedBytes);
            Assert.Equal(
                RuntimePlan.Resolve(options).ScanMemoryPoolBytes,
                duringScan.ScanBufferCapacityBytes);
        }

        var afterScan = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, afterScan.ScanBufferUsedBytes);
        Assert.True(afterScan.ScanBufferPeakBytes > 0);
    }

    [Fact]
    public async Task ShouldCountActualWalAndSstBytesWrittenByThisDatabaseSession()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), new byte[1024]);
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var afterCommit = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(afterCommit.WalBytesWrittenTotal > 0);
        Assert.Equal(0, afterCommit.SstBytesWrittenTotal);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var afterFlush = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(afterCommit.WalBytesWrittenTotal, afterFlush.WalBytesWrittenTotal);
        Assert.True(afterFlush.SstBytesWrittenTotal > 0);
    }
}
