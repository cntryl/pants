using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsSnapshotLifetimeWarningTests
{
    [Fact]
    public async Task ShouldWarnOnceForEachLongLivedSnapshotWithoutEvictingIt()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory()
                .WithTtlClock(clock)
                .WithLongLivedSnapshotWarningThreshold(TimeSpan.FromSeconds(10)));
        await using var first = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var second = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        long warnings = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Pants" &&
                    instrument.Name == "pants.snapshots.lifetime_warnings")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref warnings, measurement));
        listener.Start();

        var beforeThreshold = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, beforeThreshold.LongLivedSnapshots);
        Assert.Equal(0, beforeThreshold.LongLivedSnapshotWarningsTotal);

        clock.UtcNow += TimeSpan.FromSeconds(11);
        var firstWarning = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(2, firstWarning.ActiveSnapshots);
        Assert.Equal(2, firstWarning.LongLivedSnapshots);
        Assert.Equal(2, firstWarning.LongLivedSnapshotWarningsTotal);
        Assert.Equal(2, warnings);

        var repeatedRead = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(2, repeatedRead.ActiveSnapshots);
        Assert.Equal(2, repeatedRead.LongLivedSnapshots);
        Assert.Equal(2, repeatedRead.LongLivedSnapshotWarningsTotal);
        Assert.Equal(2, warnings);

        await first.DisposeAsync();
        var afterOneSnapshotIsDisposed = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(1, afterOneSnapshotIsDisposed.ActiveSnapshots);
        Assert.Equal(1, afterOneSnapshotIsDisposed.LongLivedSnapshots);
        Assert.Equal(2, afterOneSnapshotIsDisposed.LongLivedSnapshotWarningsTotal);
    }

    [Fact]
    public async Task ShouldEvaluateSnapshotLifetimeWhileCompactionMetricsUseTheLivePath()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionManifestPublish);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "snapshot-lifetime-compaction/")
            .WithBackgroundCompaction(false)
            .WithTtlClock(clock)
            .WithLongLivedSnapshotWarningThreshold(TimeSpan.FromSeconds(10));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await CommitCloudAndFlushAsync(database, "first");
        await CommitCloudAndFlushAsync(database, "second");
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        clock.UtcNow += TimeSpan.FromSeconds(11);

        Task? compaction = null;
        try
        {
            compaction = database.Maintenance.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);

            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

            Assert.Equal(1, metrics.ActiveSnapshots);
            Assert.Equal(1, metrics.LongLivedSnapshots);
            Assert.Equal(1, metrics.LongLivedSnapshotWarningsTotal);
        }
        finally
        {
            failpoint.Release();
            if (compaction is not null)
            {
                await compaction.WaitAsync(TestTimeouts.Expected);
            }
        }
    }

    [Fact]
    public async Task ShouldWarnForLongLivedScanSnapshotAfterItsTransactionCompletes()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory()
                .WithTtlClock(clock)
                .WithLongLivedSnapshotWarningThreshold(TimeSpan.FromSeconds(10)));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());

        await transaction.DisposeAsync();
        clock.UtcNow += TimeSpan.FromSeconds(11);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(1, metrics.ActiveSnapshots);
        Assert.Equal(1, metrics.LongLivedSnapshots);
        Assert.Equal(1, metrics.LongLivedSnapshotWarningsTotal);
        await scan.DisposeAsync();
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    [Fact]
    public async Task ShouldKeepUndisposedTransactionSnapshotPinnedAfterGarbageCollection()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var instance = Assert.IsType<DatabaseInstance>(database);
        var transactionReference = await BeginUndisposedTransactionAsync(database);
        var transactionId = Assert.Single(instance.Telemetry.ActiveSnapshotStartTimes).Key;

        CollectGarbage();

        try
        {
            Assert.False(transactionReference.TryGetTarget(out _));
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ShutdownAsync(TestTimeouts.Expected).AsTask());
        }
        finally
        {
            await instance.RollbackTransactionAsync(transactionId, CancellationToken.None);
        }

        await database.ShutdownAsync(TestTimeouts.Expected);
    }

    [Fact]
    public void ShouldDefaultSnapshotLifetimeWarningThresholdToOneHour()
    {
        Assert.Equal(
            TimeSpan.FromHours(1),
            PantsRuntimeConfiguration.Default.LongLivedSnapshotWarningThreshold);
        Assert.Equal(
            TimeSpan.FromHours(1),
            RuntimePlan.Resolve(PantsOpenOptions.InMemory()).LongLivedSnapshotWarningThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveSnapshotLifetimeWarningThreshold(long ticks)
    {
        var exception = Assert.Throws<PantsInvalidArgumentException>(() =>
            RuntimePlan.Resolve(PantsOpenOptions.InMemory()
                .WithLongLivedSnapshotWarningThreshold(TimeSpan.FromTicks(ticks))));

        Assert.Contains("LongLivedSnapshotWarningThreshold", exception.Message, StringComparison.Ordinal);
    }

    static async ValueTask CommitCloudAndFlushAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(System.Text.Encoding.UTF8.GetBytes(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<WeakReference<IPantsTransaction>> BeginUndisposedTransactionAsync(
        IPantsDatabase database)
    {
        IPantsTransaction transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var transactionReference = new WeakReference<IPantsTransaction>(transaction);
        transaction = null!;
        return transactionReference;
    }

    static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
