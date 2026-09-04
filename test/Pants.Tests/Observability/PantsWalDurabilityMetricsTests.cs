using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsWalDurabilityMetricsTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldClearPendingWritesAndAdvanceFrontiersGivenSyncCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await CommitAsync(database, "sync", PantsWriteOptions.Sync);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.CurrentSequence > 0);
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount + 1, metrics.WalFsyncCount);
        Assert.Equal(0, metrics.WalPendingWrites);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLocalDurableSequence);
    }

    [Fact]
    public async Task ShouldRetainPendingWritesAndFrontiersGivenBufferedCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await CommitAsync(database, "buffered", PantsWriteOptions.Buffered);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.CurrentSequence > before.CurrentSequence);
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
        Assert.Equal(1, metrics.WalPendingWrites);
        Assert.Equal(before.WalLastSyncedSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(before.WalLocalDurableSequence, metrics.WalLocalDurableSequence);
    }

    [Fact]
    public async Task ShouldLeaveWalAccountingUnchangedGivenBestEffortCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await CommitAsync(database, "best-effort", PantsWriteOptions.BestEffort);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.CurrentSequence > before.CurrentSequence);
        Assert.Equal(before.WalPendingWrites, metrics.WalPendingWrites);
        Assert.Equal(before.WalLastSyncedSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(before.WalLocalDurableSequence, metrics.WalLocalDurableSequence);
        Assert.Equal(before.WalAppendCount, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
    }

    [Theory]
    [InlineData(nameof(Failpoint.AfterWalAppend))]
    [InlineData(nameof(Failpoint.BeforeWalFlush))]
    public async Task ShouldCountAppendBeforeLaterBufferedWalFailure(string failureName)
    {
        using var directory = new TemporaryDirectory();
        var failure = Enum.Parse<Failpoint>(failureName);
        var failpoints = new WalBoundaryMetricsFailpointHandler(
            failure,
            TimeSpan.FromMilliseconds(50));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("failed"u8.ToArray(), "value"u8.ToArray());
        await Assert.ThrowsAsync<PantsIOException>(() => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.True(
            metrics.WalAppendNanosecondsTotal >=
            before.WalAppendNanosecondsTotal + 25_000_000);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
    }

    [Fact]
    public async Task ShouldCountOnlyFsyncWithoutAppendGivenSyncAssertionOnlyCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await CommitAsync(database, "asserted", PantsWriteOptions.Buffered);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.AssertValue("asserted"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount + 1, metrics.WalFsyncCount);
    }

    [Fact]
    public async Task ShouldCountEveryPhysicalWalRecordGivenSpilledBufferedCommit()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1024))
            .WithMemtableLimits(24 * 1024)
            .WithTransactionMemoryPool(1024);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        const int operationCount = 6;
        for (var index = 0; index < operationCount; index++)
        {
            transaction.Put(TestBytes.FromString($"spill-{index}"), new byte[900]);
        }

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(operationCount + 2, metrics.WalPendingWrites);
        Assert.Equal(0, metrics.WalLastSyncedSequence);
        Assert.Equal(0, metrics.WalLocalDurableSequence);
    }

    [Fact]
    public async Task ShouldRestoreWalFrontiersAndCurrentSegmentGivenReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithWalBufferSize(1);
        long committedSequence;
        long currentSegment;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, "recovered", PantsWriteOptions.Sync);
            var committed = await database.Diagnostics.GetRuntimeMetricsAsync();
            committedSequence = committed.CurrentSequence;
            currentSegment = committed.WalCurrentSegmentId;
            Assert.True(currentSegment > 1);
            Assert.Equal(1, committed.WalFsyncCount);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(currentSegment, metrics.WalCurrentSegmentId);
        Assert.Equal(0, metrics.WalPendingWrites);
        Assert.Equal(0, metrics.WalLastSyncedSequence);
        Assert.Equal(committedSequence, metrics.WalLocalDurableSequence);
    }

    [Fact]
    public async Task ShouldCountPhysicalFsyncGivenBufferedCommitRotatesWal()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithWalBufferSize(1));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await CommitAsync(database, "buffered-rotation", PantsWriteOptions.Buffered);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount + 1, metrics.WalFsyncCount);
        Assert.True(metrics.WalFsyncNanosecondsTotal > 0);
        Assert.Equal(0, metrics.WalPendingWrites);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLocalDurableSequence);
    }

    [Fact]
    public async Task ShouldCountTwoFlushesWithoutFsyncGivenCloudAsyncSeal()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "wal-flush-metrics/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await CommitAsync(database, "cloud-seal", PantsWriteOptions.CloudAsync);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount + 2, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
        Assert.Equal(1, metrics.CloudAsyncWalSegmentsSealed);
        Assert.Equal(0, metrics.WalPendingWrites);
    }

    [Fact]
    public async Task ShouldRetainPendingWritesGivenCloudStrictSealFailsBeforeRotation()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new RetryingCloudWalSealFailpointHandler();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "strict-seal-metrics/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoints));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        try
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("strict-failure"u8.ToArray(), "value"u8.ToArray());
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
            await failpoints.WaitForFailureAsync(AssertionTimeout);

            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var value = await reader.GetAsync("strict-failure"u8.ToArray());

            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(
                "value",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
            Assert.Equal(before.WalPendingWrites + 1, metrics.WalPendingWrites);
            Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
            Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);

            await failpoints.WaitForRetryAsync(AssertionTimeout);
        }
        finally
        {
            failpoints.AllowSuccess();
        }

        using var timeout = new CancellationTokenSource(AssertionTimeout);
        while (true)
        {
            var recovered = await database.Diagnostics.GetRuntimeMetricsAsync(timeout.Token);
            if (recovered.WalPendingWrites == 0 &&
                recovered.WalCloudDurableSequence >= recovered.CurrentSequence)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    [Fact]
    public async Task ShouldMeasureAppendAndFsyncAtSeparatePhysicalBoundaries()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new WalAppendDelayFailpointHandler(TimeSpan.FromMilliseconds(100));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(failpoints));

        await CommitAsync(database, "timed", PantsWriteOptions.Sync);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.WalAppendNanosecondsTotal >= 50_000_000);
        Assert.True(
            metrics.WalFsyncNanosecondsTotal < metrics.WalAppendNanosecondsTotal,
            $"Expected fsync {metrics.WalFsyncNanosecondsTotal} ns to exclude " +
            $"the delayed append {metrics.WalAppendNanosecondsTotal} ns.");
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        string key,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }
}
