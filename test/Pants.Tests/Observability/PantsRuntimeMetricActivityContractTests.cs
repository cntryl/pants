namespace Cntryl.Pants.Tests;

public sealed class PantsRuntimeMetricActivityContractTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ShouldReportOldestSnapshotAgeGivenTtlClockAdvances()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        await using var snapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        clock.UtcNow += TimeSpan.FromSeconds(7);

        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.Equal(7, metrics.OldestSnapshotAgeSeconds);
        clock.UtcNow -= TimeSpan.FromSeconds(5);
        Assert.Equal(7, (await database.GetRuntimeMetricsAsync()).OldestSnapshotAgeSeconds);
    }

    [Fact]
    public async Task ShouldMeasureActiveAndCompletedWriteStallGivenClockAdvances()
    {
        const int flushThresholdBytes = 128 * 1024;
        const int keyAndEntryOverheadBytes = 65;
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timeProvider = new ManualTimeProvider();
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            PantsFailpoint.BeforeFlushManifestPublish);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024 * 1024))
            .WithMemtableLimits(512 * 1024, flushThresholdBytes)
            .WithTransactionMemoryPool(512 * 1024)
            .WithBackgroundCompaction(false)
            .WithTtlClock(clock);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(
                failpoint,
                runtimeTimeProvider: timeProvider));
        var family = await database.CreateColumnFamilyAsync("timed-stall");
        var value = new byte[flushThresholdBytes - keyAndEntryOverheadBytes];
        try
        {
            await CommitAsync(database, family, new byte[] { 1 }, value);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(database, family, new byte[] { 2 }, value);
            _ = await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                CommitAsync(database, family, new byte[] { 3 }, value).AsTask());

            timeProvider.Advance(TimeSpan.FromSeconds(3));

            var active = await database.GetRuntimeMetricsAsync();
            Assert.Equal(3_000_000_000, active.WriteStallActiveNanoseconds);
            Assert.Equal(3_000_000_000, active.WriteStallNanosecondsTotal);
            Assert.Equal(3_000_000_000, active.WriteStallNanosecondsMaximum);

            timeProvider.Advance(TimeSpan.FromSeconds(-5));
            var afterBackwardMovement = await database.GetRuntimeMetricsAsync();
            Assert.Equal(3_000_000_000, afterBackwardMovement.WriteStallActiveNanoseconds);
            Assert.Equal(3_000_000_000, afterBackwardMovement.WriteStallNanosecondsTotal);
            Assert.Equal(3_000_000_000, afterBackwardMovement.WriteStallNanosecondsMaximum);

            clock.UtcNow -= TimeSpan.FromSeconds(2);
            failpoint.Release();
            Assert.True(await database.WaitForWriteStallClearAsync(family, AssertionTimeout));
            var completed = await database.GetRuntimeMetricsAsync();
            Assert.Equal(0, completed.WriteStallActiveNanoseconds);
            Assert.Equal(3_000_000_000, completed.WriteStallNanosecondsTotal);
            Assert.Equal(3_000_000_000, completed.WriteStallNanosecondsMaximum);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRetainWriteStallDurationGivenStallClearsBetweenMetricReads()
    {
        const int flushThresholdBytes = 128 * 1024;
        const int keyAndEntryOverheadBytes = 65;
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timeProvider = new ManualTimeProvider();
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            PantsFailpoint.BeforeFlushManifestPublish);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024 * 1024))
            .WithMemtableLimits(512 * 1024, flushThresholdBytes)
            .WithTransactionMemoryPool(512 * 1024)
            .WithBackgroundCompaction(false)
            .WithTtlClock(clock);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(
                failpoint,
                runtimeTimeProvider: timeProvider));
        var family = await database.CreateColumnFamilyAsync("between-reads");
        var value = new byte[flushThresholdBytes - keyAndEntryOverheadBytes];
        try
        {
            _ = await database.GetRuntimeMetricsAsync();
            await CommitAsync(database, family, new byte[] { 1 }, value);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(database, family, new byte[] { 2 }, value);

            timeProvider.Advance(TimeSpan.FromSeconds(4));
            failpoint.Release();
            Assert.True(await database.WaitForWriteStallClearAsync(family, AssertionTimeout));

            var completed = await database.GetRuntimeMetricsAsync();
            Assert.Equal(4_000_000_000, completed.WriteStallNanosecondsTotal);
            Assert.Equal(4_000_000_000, completed.WriteStallNanosecondsMaximum);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldExposeLiveCompactionWorkGivenWorkerIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            PantsFailpoint.BeforeCompactionManifestPublish);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        await CommitAndFlushAsync(database, "first");
        await CommitAndFlushAsync(database, "second");
        Task? firstCompaction = null;
        Task? pendingCompaction = null;
        Task<PantsRuntimeMetrics>? metricsRequest = null;
        try
        {
            firstCompaction = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            pendingCompaction = database.CompactAllAsync().AsTask();

            metricsRequest = database.GetRuntimeMetricsAsync().AsTask();

            Assert.True(metricsRequest.IsCompleted);
            var metrics = await metricsRequest;
            Assert.Equal(1, metrics.ActiveCompactions);
            Assert.Equal(2, metrics.CompactingSsts);
            Assert.Equal(1, metrics.PendingCompactions);
        }
        finally
        {
            failpoint.Release();
            if (firstCompaction is not null)
            {
                await firstCompaction.WaitAsync(AssertionTimeout);
            }

            if (pendingCompaction is not null)
            {
                await pendingCompaction.WaitAsync(AssertionTimeout);
            }

            if (metricsRequest is not null)
            {
                _ = await metricsRequest.WaitAsync(AssertionTimeout);
            }
        }
    }

    [Fact]
    public async Task ShouldExposePendingCloudUploadGivenPublicationIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(PantsFailpoint.AfterCloudUpload);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "metrics-pending-cloud/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("pending"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        Task? flush = null;
        Task<PantsRuntimeMetrics>? metricsRequest = null;
        try
        {
            flush = database.FlushAsync(database.DefaultColumnFamily).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            metricsRequest = database.GetRuntimeMetricsAsync().AsTask();

            Assert.True(metricsRequest.IsCompleted);
            Assert.Equal(1, (await metricsRequest).PendingCloudUploads);
        }
        finally
        {
            failpoint.Release();
            if (flush is not null)
            {
                await flush.WaitAsync(AssertionTimeout);
            }

            if (metricsRequest is not null)
            {
                _ = await metricsRequest.WaitAsync(AssertionTimeout);
            }
        }
    }

    [Fact]
    public async Task ShouldCountCloudWriteStallGivenUploadQueueIsFull()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(PantsFailpoint.AfterCloudWalUpload);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "metrics-cloud-stall/")
            .WithCoordinatorQueueCapacityForTesting(1)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: 1))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        await CommitCloudAsync(database, "occupy-upload-queue");
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        await using var blocked = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        blocked.Put("blocked"u8.ToArray(), "value"u8.ToArray());
        var commit = blocked.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();
        try
        {
            var metrics = await database.GetRuntimeMetricsAsync();
            Assert.Equal(1, metrics.WriteStallsCloudTotal);
            Assert.Equal(1, metrics.WriteStallsTotal);
        }
        finally
        {
            failpoint.Release();
        }

        _ = await Assert.ThrowsAsync<PantsWriteStallException>(() => commit);
    }

    [Fact]
    public async Task ShouldRejectCloudWritePromptlyGivenTwoUploadObligationsFillCapacity()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(PantsFailpoint.AfterCloudWalUpload);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "metrics-cloud-stall-capacity-two/")
            .WithCoordinatorQueueCapacityForTesting(2)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: long.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: 1))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        await CommitCloudAsync(database, "first-upload");
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        try
        {
            await CommitCloudAsync(database, "second-upload");
            var saturated = await database.GetRuntimeMetricsAsync();
            Assert.Equal(2, saturated.PendingCloudUploads);
            Assert.Equal(0, saturated.WriteStallsCloudTotal);

            await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                CommitCloudAsync(database, "rejected-upload")
                    .AsTask()
                    .WaitAsync(AssertionTimeout));

            var stalled = await database.GetRuntimeMetricsAsync();
            Assert.Equal(2, stalled.PendingCloudUploads);
            Assert.Equal(1, stalled.WriteStallsCloudTotal);
            Assert.Equal(1, stalled.WriteStallsTotal);
        }
        finally
        {
            failpoint.Release();
        }

        using var timeout = new CancellationTokenSource(AssertionTimeout);
        PantsRuntimeMetrics drained;
        do
        {
            drained = await database.GetRuntimeMetricsAsync(timeout.Token);
            if (drained.PendingCloudUploads != 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
            }
        }
        while (drained.PendingCloudUploads != 0);

        Assert.Equal(1, drained.WriteStallsCloudTotal);
    }

    [Fact]
    public async Task ShouldCountCompactionWriteStallGivenHybridCompactionQueueIsFull()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            PantsFailpoint.BeforeCompactionManifestPublish);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "metrics-compaction-stall/")
            .WithCoordinatorQueueCapacityForTesting(1)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        await CommitCloudAndFlushAsync(database, "first");
        await CommitCloudAndFlushAsync(database, "second");
        await using var blocked = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        blocked.Put("blocked"u8.ToArray(), "value"u8.ToArray());
        var compaction = database.CompactAllAsync().AsTask();
        Task? commit = null;
        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            commit = blocked.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();

            var metrics = await database.GetRuntimeMetricsAsync();
            Assert.Equal(1, metrics.WriteStallsCompactionTotal);
            Assert.Equal(1, metrics.WriteStallsTotal);
        }
        finally
        {
            failpoint.Release();
            await compaction.WaitAsync(AssertionTimeout);
        }

        Assert.NotNull(commit);
        _ = await Assert.ThrowsAsync<PantsWriteStallException>(() => commit);
    }

    [Fact]
    public async Task ShouldExposePendingHybridEvictionGivenEvictionIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            PantsFailpoint.BeforeHybridSstEviction);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "metrics-hybrid-eviction/")
            .WithSimulatedCloudLocalStorageBudget(128 * 1024)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoint));
        var value = new byte[256 * 1024];
        new Random(17).NextBytes(value);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("evict"u8.ToArray(), value);
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        var flush = database.FlushAsync(database.DefaultColumnFamily).AsTask();
        Task<PantsRuntimeMetrics>? metricsRequest = null;
        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            metricsRequest = database.GetRuntimeMetricsAsync().AsTask();

            Assert.True(metricsRequest.IsCompleted);
            Assert.True((await metricsRequest).HybridPendingEvictions > 0);
        }
        finally
        {
            failpoint.Release();
            await flush.WaitAsync(AssertionTimeout);
            if (metricsRequest is not null)
            {
                _ = await metricsRequest.WaitAsync(AssertionTimeout);
            }
        }

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).HybridPendingEvictions);
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async ValueTask CommitAndFlushAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.FlushAsync(database.DefaultColumnFamily);
    }

    static async ValueTask CommitCloudAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }

    static async ValueTask CommitCloudAndFlushAsync(IPantsDatabase database, string key)
    {
        await CommitCloudAsync(database, key);
        await database.FlushAsync(database.DefaultColumnFamily);
    }
}
