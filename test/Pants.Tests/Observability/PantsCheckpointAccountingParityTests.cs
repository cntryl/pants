using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsCheckpointAccountingParityTests
{
    const int RowCount = 32;
    const int ValueBytes = 128;

    [Fact]
    public async Task ShouldReleaseActualEngineLeaseWhenRetainedCheckpointMetricsOutliveShutdown()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(8 * 1024 * 1024);
        PantsRuntimeMetrics retainedMetrics;
        (
            long CurrentSequence,
            int SstCount,
            long SstBytes,
            long SstBytesWrittenTotal,
            long FlushBuildCount,
            long FlushPublishCount,
            long FlushPublishNanosecondsTotal) retainedSnapshot;

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < RowCount; index++)
                {
                    transaction.Put(RowKeyBytes(index), RowValue(index));
                }

                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await AssertRowsAsync(database);
            retainedMetrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(1, retainedMetrics.SstCount);
            Assert.True(retainedMetrics.SstBytes > 0);
            Assert.True(retainedMetrics.SstBytesWrittenTotal > before.SstBytesWrittenTotal);
            Assert.Equal(before.FlushBuildCount + 1, retainedMetrics.FlushBuildCount);
            Assert.Equal(before.FlushPublishCount + 1, retainedMetrics.FlushPublishCount);
            Assert.True(retainedMetrics.FlushPublishNanosecondsTotal > 0);
            retainedSnapshot = (
                retainedMetrics.CurrentSequence,
                retainedMetrics.SstCount,
                retainedMetrics.SstBytes,
                retainedMetrics.SstBytesWrittenTotal,
                retainedMetrics.FlushBuildCount,
                retainedMetrics.FlushPublishCount,
                retainedMetrics.FlushPublishNanosecondsTotal);

            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            Assert.True(reopened.PersistentStorage!.IsPrimaryLeaseHealthy);
            await AssertRowsAsync(reopened);
        }

        Assert.Equal(
            retainedSnapshot,
            (
                retainedMetrics.CurrentSequence,
                retainedMetrics.SstCount,
                retainedMetrics.SstBytes,
                retainedMetrics.SstBytesWrittenTotal,
                retainedMetrics.FlushBuildCount,
                retainedMetrics.FlushPublishCount,
                retainedMetrics.FlushPublishNanosecondsTotal));
    }

    static async Task AssertRowsAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < RowCount; index++)
        {
            var value = await transaction.GetAsync(RowKeyBytes(index));
            Assert.Equal(RowValue(index), value?.ToArray());
        }

        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var count = 0;
        await foreach (var row in scan)
        {
            Assert.Equal(RowKeyBytes(count), row.Key.ToArray());
            Assert.Equal(RowValue(count), row.Value.ToArray());
            count++;
        }

        Assert.Equal(RowCount, count);
    }

    static byte[] RowKeyBytes(int index) => TestBytes.FromString($"row-{index:0000}");

    static byte[] RowValue(int index) => Enumerable.Repeat((byte)index, ValueBytes).ToArray();
}
