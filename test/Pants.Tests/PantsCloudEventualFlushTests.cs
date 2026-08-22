namespace Pants.Tests;

public sealed class PantsCloudEventualFlushTests
{
    [Fact]
    public async Task ShouldBatchWritesWhenUsingCloudMode()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 128, maximumPendingWrites: 4);
        await using var database = await OpenAsync(directory.Path, policy);

        for (var index = 0; index < 3; index++)
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                $"batched-{index}",
                PantsWriteOptions.CloudAsync);
        }

        var beforeThreshold = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, beforeThreshold.WalCurrentSegmentId);
        Assert.Equal(3, beforeThreshold.WalPendingWrites);

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "batched-3",
            PantsWriteOptions.CloudAsync);

        var sealedMetrics = await database.GetRuntimeMetricsAsync();
        Assert.True(sealedMetrics.WalCurrentSegmentId > beforeThreshold.WalCurrentSegmentId);
        Assert.Equal(0, sealedMetrics.WalPendingWrites);
    }

    [Fact]
    public async Task ShouldFlushCloudSegmentsOnShutdown()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 128, maximumPendingWrites: int.MaxValue);
        var options = CreateOptions(directory.Path, policy);
        await using var database = await PantsDatabase.OpenAsync(options);
        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "shutdown",
            PantsWriteOptions.CloudAsync);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));

        await database.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ShouldSealSubthresholdCloudAsyncWalAtMaximumDelay()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(
            segmentGap: 128,
            maximumPendingWrites: int.MaxValue,
            maximumDelay: TimeSpan.FromMilliseconds(50));
        await using var database = await OpenAsync(directory.Path, policy);

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "deadline",
            PantsWriteOptions.CloudAsync);

        var metrics = await WaitForMetricsAsync(
            database,
            static candidate => candidate.WalCurrentSegmentId > 1);
        Assert.Equal(0, metrics.WalPendingWrites);
    }

    [Fact]
    public async Task ShouldEventuallyPublishSstGivenManyCloudBufferedWritesWhenMemtableNeverReachesSizeThreshold()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 4, maximumPendingWrites: 1);
        await using var database = await OpenAsync(directory.Path, policy);

        for (var index = 0; index < 3; index++)
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                $"buffered-{index}",
                PantsWriteOptions.CloudAsync);
        }

        var beforeGap = await database.GetRuntimeMetricsAsync();
        Assert.Equal(3, beforeGap.MaximumMemtableWalSegmentGap);
        Assert.Equal(0, beforeGap.SstCount);

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "buffered-3",
            PantsWriteOptions.CloudAsync);

        var flushed = await database.GetRuntimeMetricsAsync();
        Assert.True(flushed.SstCount >= 1);
        Assert.True(flushed.MaximumMemtableWalSegmentGap < 4);
    }

    [Fact]
    public async Task ShouldEventuallyPublishSstGivenManyCloudStrictWritesWhenMemtableNeverReachesSizeThreshold()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 4, maximumPendingWrites: 10_000);
        await using var database = await OpenAsync(directory.Path, policy);

        for (var index = 0; index < 4; index++)
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                $"strict-{index}",
                PantsWriteOptions.CloudStrict);
        }

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.True(metrics.SstCount >= 1);
        Assert.True(metrics.MaximumMemtableWalSegmentGap < 4);
    }

    [Fact]
    public async Task ShouldPublishLightlyWrittenColumnFamilyGivenBusyNeighborWhenCloudSegmentGapFlushRuns()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 4, maximumPendingWrites: 1);
        await using var database = await OpenAsync(directory.Path, policy);
        var light = await database.CreateColumnFamilyAsync("light");
        var busy = await database.CreateColumnFamilyAsync("busy");
        await CommitAsync(database, light, "light", PantsWriteOptions.CloudAsync);

        for (var index = 0; index < 3; index++)
        {
            await CommitAsync(
                database,
                busy,
                $"busy-{index}",
                PantsWriteOptions.CloudAsync);
        }

        var layout = await database.GetStorageLayoutAsync();
        Assert.Contains(
            layout.Levels.SelectMany(static level => level.Files),
            file => file.ColumnFamilyId == light.Id);
    }

    [Fact]
    public async Task ShouldResetMemtableWalGapAfterReopenBeforeNewSegmentChurn()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(segmentGap: 128, maximumPendingWrites: 1);
        var options = CreateOptions(directory.Path, policy);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 4; index++)
            {
                await CommitAsync(
                    database,
                    database.DefaultColumnFamily,
                    $"reopen-{index}",
                    PantsWriteOptions.CloudAsync);
            }

            Assert.True((await database.GetRuntimeMetricsAsync()).WalCurrentSegmentId > 4);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var metrics = await reopened.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.MaximumMemtableWalSegmentGap);
        Assert.Equal(0, metrics.SstCount);
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(System.Text.Encoding.UTF8.GetBytes(key), "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var metrics = await database.GetRuntimeMetricsAsync(timeout.Token);
            if (predicate(metrics))
            {
                return metrics;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    static PantsCloudWritePolicy CreatePolicy(
        long segmentGap,
        int maximumPendingWrites,
        TimeSpan? maximumDelay = null) => new(
        EventualFlushSegmentGap: segmentGap,
        WalSealMinimumSegmentBytes: long.MaxValue,
        WalSealMaximumFlushDelay: maximumDelay ?? TimeSpan.FromHours(1),
        WalSealMaximumPendingWrites: maximumPendingWrites);

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        PantsCloudWritePolicy policy) =>
        PantsDatabase.OpenAsync(CreateOptions(path, policy));

    static PantsOpenOptions CreateOptions(string path, PantsCloudWritePolicy policy) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "eventual-flush/")
            .WithCloudWritePolicy(policy)
            .WithBackgroundCompaction(false);
}
