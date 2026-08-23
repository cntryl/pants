namespace Cntryl.Pants.Tests;

public sealed class PantsTelemetryContractTests
{
    [Fact]
    public async Task ShouldReportSyncedWalFrontierAfterSyncCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("synced"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.True(metrics.CurrentSequence > 0);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.True(metrics.WalLocalDurableSequence >= metrics.WalLastSyncedSequence);
    }

    [Fact]
    public async Task ShouldReportCloudAsyncWalLifecycle()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "telemetry-cloud-wal/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: 1))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);

        await CommitAsync(database, "cloud-async", PantsWriteOptions.CloudAsync);
        var metrics = await WaitForMetricsAsync(
            database,
            static candidate =>
                candidate.CurrentSequence > 0 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence &&
                candidate.PendingCloudUploads == 0 &&
                candidate.CloudAsyncWalAcknowledgementLatencyMicroseconds > 0);

        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(metrics.CurrentSequence, metrics.WalCloudDurableSequence);
        Assert.Equal(1, metrics.CloudAsyncWalSegmentsSealed);
        Assert.True(metrics.CloudAsyncWalBytesSealed > 0);
        Assert.True(metrics.CloudAsyncWalSealLatencyMicroseconds > 0);
        Assert.Equal(1, metrics.CloudAsyncWalUploadsStarted);
        Assert.Equal(1, metrics.CloudAsyncWalUploadsCompleted);
        Assert.True(metrics.CloudAsyncWalUploadLatencyMicroseconds > 0);
        Assert.True(metrics.CloudAsyncWalAcknowledgementLatencyMicroseconds > 0);
        Assert.Equal(0, metrics.CloudAsyncWalUploadsFailed);
    }

    [Fact]
    public async Task ShouldRetrySaturatedCloudAsyncWalUploadGivenTransientRawIoFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new RetryingCloudWalUploadFailpointHandler();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "telemetry-cloud-wal-failure/")
            .WithCoordinatorQueueCapacityForTesting(1)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: 1))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));

        try
        {
            await CommitAsync(database, "cloud-async-failure", PantsWriteOptions.CloudAsync);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await failpoints.WaitForFailureAsync(timeout.Token);
            var failed = await WaitForMetricsAsync(
                database,
                static candidate =>
                    candidate.CloudAsyncWalUploadsFailed >= 1 &&
                    candidate.Health == PantsEngineHealth.Degraded);

            Assert.Equal(PantsEngineHealth.Degraded, failed.Health);
            Assert.Equal(1, failed.CloudAsyncWalSegmentsSealed);
            Assert.True(failed.CloudAsyncWalUploadsStarted >= 1);
            Assert.Equal(0, failed.CloudAsyncWalUploadsCompleted);
            Assert.True(failed.CloudAsyncWalUploadsFailed >= 1);
            Assert.Equal(1, failed.PendingCloudUploads);
            Assert.Equal(0, failed.WalCloudDurableSequence);

            failpoints.AllowSuccess();
            var recovered = await WaitForMetricsAsync(
                database,
                static candidate =>
                    candidate.PendingCloudUploads == 0 &&
                    candidate.WalCloudDurableSequence == candidate.CurrentSequence);

            Assert.Equal(PantsEngineHealth.Degraded, recovered.Health);
            Assert.True(failpoints.FailureCount >= 1);
            await CommitAsync(database, "after-retry", PantsWriteOptions.CloudAsync);
        }
        finally
        {
            failpoints.AllowSuccess();
        }
    }

    [Fact]
    public async Task ShouldReportFlushRetryAfterTransientCloudPublicationFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CloudCompactionFailpointHandler();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "telemetry-flush-retry/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: int.MaxValue))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));
        await CommitAsync(database, "retry", PantsWriteOptions.CloudAsync);
        failpoints.Arm(PantsFailpoint.BeforeCloudUpload);

        await Assert.ThrowsAsync<PantsIOException>(
            () => database.FlushAsync(database.DefaultColumnFamily).AsTask());
        await WaitForAsync(() => Directory
            .EnumerateFiles(
                Path.Combine(directory.Path, "cloud_store", "sst"),
                "*.sst",
                SearchOption.TopDirectoryOnly)
            .Any());
        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.True(metrics.FlushFailuresTotal >= 1);
        Assert.True(metrics.FlushRetriesTotal >= 1);
    }

    [Fact]
    public async Task ShouldNotAttributeDdlWorkerFailureToFlushMetrics()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new DdlFailpointHandler("BeforeDdlRemoteCas");
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "telemetry-ddl-failure/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            database.CreateColumnFamilyAsync("failed-ddl").AsTask());

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).FlushFailuresTotal);
    }

    [Fact]
    public async Task ShouldReportPerDatabaseReadPathActivity()
    {
        await using IPantsDatabase first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsDatabase second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        await using (IPantsTransaction transaction = await first.BeginTransactionAsync(
                         first.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        PantsReadPathDiagnostics firstDiagnostics = await first.GetReadPathDiagnosticsAsync();
        PantsReadPathDiagnostics secondDiagnostics = await second.GetReadPathDiagnosticsAsync();

        Assert.Equal(1, firstDiagnostics.ReadOnlyTransactionsBegun);
        Assert.Equal(1, firstDiagnostics.ReadOnlySnapshotCacheHits);
        Assert.Equal(1, firstDiagnostics.SnapshotsRegistered);
        Assert.Equal(1, firstDiagnostics.SnapshotsUnregistered);
        Assert.Equal(1, (await first.GetReadAmplificationMetricsAsync()).ReadsTotal);
        Assert.Equal(new PantsReadPathDiagnostics(), secondDiagnostics);
    }

    [Fact]
    public async Task ShouldClassifyPointWriteCoveredByRangeAsPointConflict()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction pointWriter = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        pointWriter.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        pointWriter.Put("key"u8.ToArray(), "first"u8.ToArray());

        await using (IPantsTransaction rangeWriter = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rangeWriter.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await rangeWriter.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            pointWriter.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(1, metrics.WriteConflictsPointTotal);
        Assert.Equal(0, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldReportBloomRejectionsAndDataBlockReads()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (int index = 0; index < 64; index++)
            {
                writer.Put(TestBytes.FromString($"key-{index:D4}"), new byte[1024]);
            }

            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync("key-0001"u8.ToArray()));
        Assert.NotNull(await reader.GetAsync("key-0001"u8.ToArray()));
        for (int index = 0; index < 32; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"key-{index:D4}-absent")));
        }

        PantsReadPathDiagnostics diagnostics = await database.GetReadPathDiagnosticsAsync();
        PantsReadAmplificationMetrics amplification = await database.GetReadAmplificationMetricsAsync();
        PantsRuntimeMetrics runtime = await database.GetRuntimeMetricsAsync();
        Assert.True(diagnostics.BloomChecks >= 33);
        Assert.True(diagnostics.BloomRejects > 0);
        Assert.True(diagnostics.DataBlocksRead < diagnostics.BloomChecks);
        Assert.True(diagnostics.DataBlocksRead > 0);
        Assert.True(diagnostics.BloomTruePositives > 0);
        Assert.True(diagnostics.SstReaderCacheHits > 0);
        Assert.True(diagnostics.SstReaderCacheMisses > 0);
        Assert.True(diagnostics.SstBlockCacheHits > 0);
        Assert.True(diagnostics.SstBlockCacheMisses > 0);
        Assert.Equal(diagnostics.SstBlockCacheHits, runtime.CacheHits);
        Assert.Equal(diagnostics.SstBlockCacheMisses, runtime.CacheMisses);
        Assert.Equal(diagnostics.BloomChecks, runtime.SstBloomChecksTotal);
        Assert.Equal(diagnostics.BloomRejects, runtime.SstBloomRejectsTotal);
        Assert.Equal(diagnostics.BloomTruePositives, runtime.SstBloomTruePositivesTotal);
        Assert.Equal(diagnostics.BloomFalsePositives, runtime.SstBloomFalsePositivesTotal);
        Assert.Equal(diagnostics.KeyRangeRejects, runtime.SstKeyRangeRejectsTotal);
        Assert.Equal(diagnostics.DataBlocksRead, runtime.SstDataBlocksReadTotal);
        Assert.Equal(34, amplification.ReadsTotal);
        Assert.True(amplification.BlocksReadTotal > diagnostics.DataBlocksRead);
        Assert.Equal(diagnostics.SstReaderCacheHits, amplification.ReaderCacheHitsTotal);
        Assert.Equal(diagnostics.SstReaderCacheMisses, amplification.ReaderCacheMissesTotal);
        Assert.Equal(diagnostics.SstBlockCacheHits, amplification.BlockCacheHitsTotal);
        Assert.Equal(diagnostics.SstBlockCacheMisses, amplification.BlockCacheMissesTotal);
        Assert.Equal(diagnostics.BloomTruePositives, amplification.BloomTruePositivesTotal);
        Assert.Equal(diagnostics.BloomFalsePositives, amplification.BloomFalsePositivesTotal);
        Assert.Equal(diagnostics.BloomTrueNegatives, amplification.BloomTrueNegativesTotal);
        Assert.Equal(diagnostics.DataBlocksRead, amplification.DataBlocksReadTotal);
        Assert.True(amplification.DataBlocksReadTotal > 0);
    }

    [Fact]
    public async Task ShouldReportPhysicalSstWorkGivenFlushedRangeScan()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("a"u8.ToArray(), "one"u8.ToArray());
            writer.Put("b"u8.ToArray(), "two"u8.ToArray());
            writer.Put("c"u8.ToArray(), "three"u8.ToArray());
            writer.DeleteRange("b"u8.ToArray(), "c"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        PantsReadPathDiagnostics before = await database.GetReadPathDiagnosticsAsync();
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using IPantsScan scan = await reader.ScanAsync(new PantsScanQuery());
        var entries = new List<PantsEntry>();
        await foreach (PantsEntry entry in scan)
        {
            entries.Add(entry);
        }

        PantsReadPathDiagnostics after = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(2, entries.Count);
        Assert.True(after.CandidateSstFilesChecked > before.CandidateSstFilesChecked);
        Assert.True(after.CandidateBlocksChecked > before.CandidateBlocksChecked);
        Assert.True(after.DataBlocksRead > before.DataBlocksRead);
        Assert.True(after.RangeTombstoneScans > before.RangeTombstoneScans);
    }

    [Fact]
    public async Task ShouldLeaveHotBlockCacheContentsUnchangedGivenStreamingScan()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 128; index++)
            {
                writer.Put(TestBytes.FromString($"key-{index:0000}"), new byte[1024]);
            }

            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync("key-0064"u8.ToArray()));
        Assert.NotNull(await reader.GetAsync("key-0064"u8.ToArray()));
        PantsReadPathDiagnostics beforeScan = await database.GetReadPathDiagnosticsAsync();

        await using (IPantsScan scan = await reader.ScanAsync(new PantsScanQuery()))
        {
            await foreach (PantsEntry _ in scan)
            {
            }
        }

        PantsReadPathDiagnostics afterScan = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(beforeScan.SstBlockCacheHits, afterScan.SstBlockCacheHits);
        Assert.Equal(beforeScan.SstBlockCacheMisses, afterScan.SstBlockCacheMisses);

        Assert.NotNull(await reader.GetAsync("key-0064"u8.ToArray()));
        PantsReadPathDiagnostics afterHotRead = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(afterScan.SstBlockCacheHits + 1, afterHotRead.SstBlockCacheHits);
        Assert.Equal(afterScan.SstBlockCacheMisses, afterHotRead.SstBlockCacheMisses);
    }

    [Fact]
    public async Task ShouldGateBloomAndDiskIoWithPersistedKeyRange()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("middle"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Null(await reader.GetAsync("outside"u8.ToArray()));

        PantsReadAmplificationMetrics metrics = await database.GetReadAmplificationMetricsAsync();
        var runtime = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.KeyRangeRejectsTotal);
        Assert.Equal(1, runtime.SstKeyRangeRejectsTotal);
        Assert.Equal(0, metrics.ReaderCacheHitsTotal);
        Assert.Equal(0, metrics.ReaderCacheMissesTotal);
        Assert.Equal(0, metrics.BloomChecksTotal);
        Assert.Equal(0, metrics.DataBlocksReadTotal);
    }

    [Fact]
    public async Task ShouldClassifyBloomTrueAndFalsePositives()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 64; index++)
            {
                writer.Put(TestBytes.FromString($"key-{index:0000}"), new byte[128]);
            }

            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync("key-0032"u8.ToArray()));
        for (var index = 0; index < 2048; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"key-0032-{index:0000}")));
        }

        PantsReadAmplificationMetrics metrics = await database.GetReadAmplificationMetricsAsync();
        var runtime = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.BloomTruePositivesTotal);
        Assert.True(metrics.BloomFalsePositivesTotal > 0);
        Assert.True(metrics.BloomTrueNegativesTotal > 0);
        Assert.Equal(metrics.BloomTruePositivesTotal, runtime.SstBloomTruePositivesTotal);
        Assert.Equal(metrics.BloomFalsePositivesTotal, runtime.SstBloomFalsePositivesTotal);
        Assert.Equal(
            metrics.BloomChecksTotal,
            metrics.BloomTruePositivesTotal +
            metrics.BloomFalsePositivesTotal +
            metrics.BloomTrueNegativesTotal);
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        string key,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var metrics = await database.GetRuntimeMetricsAsync(timeout.Token);
            if (predicate(metrics))
            {
                return metrics;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    static async ValueTask WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
