using System.Text;
using Xunit.Sdk;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudEventualFlushTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldBatchWritesWhenUsingCloudMode()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(128, 4);
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
        var policy = CreatePolicy(128, int.MaxValue);
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
            128,
            int.MaxValue,
            TimeSpan.FromMilliseconds(50));
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
    public async Task ShouldRetryDeadlineSealGivenFirstCloudWalRotationFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new OneShotCloudWalSealFailureHandler();
        var policy = CreatePolicy(
            128,
            int.MaxValue,
            TimeSpan.FromMilliseconds(25));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));
        var before = await database.GetRuntimeMetricsAsync();

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "deadline-retry",
            PantsWriteOptions.CloudAsync);

        await failpoints.WaitUntilFailureInjectedAsync(AssertionTimeout);
        await failpoints.WaitUntilRetryAttemptedAsync(AssertionTimeout);
        var published = await WaitForMetricsAsync(
            database,
            candidate =>
                candidate.WalCurrentSegmentId > before.WalCurrentSegmentId &&
                candidate.WalPendingWrites == 0 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
        Assert.True(published.WalCloudDurableSequence >= published.CurrentSequence);
    }

    [Fact]
    public async Task ShouldRetryImmediateSealGivenFirstCloudWalRotationFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new OneShotCloudWalSealFailureHandler();
        var policy = CreatePolicy(
            128,
            1,
            TimeSpan.FromHours(1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));
        var before = await database.GetRuntimeMetricsAsync();

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "immediate-retry",
            PantsWriteOptions.CloudAsync);
        await failpoints.WaitUntilFailureInjectedAsync(AssertionTimeout);
        await failpoints.WaitUntilRetryAttemptedAsync(AssertionTimeout);
        var published = await WaitForMetricsAsync(
            database,
            candidate =>
                candidate.WalCurrentSegmentId > before.WalCurrentSegmentId &&
                candidate.WalPendingWrites == 0 &&
                candidate.PendingCloudUploads == 0 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);

        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
        Assert.True(published.WalCloudDurableSequence >= published.CurrentSequence);
    }

    [Fact]
    public async Task ShouldAdmitRotatedSegmentGivenPostRotationCloudSealFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new OneShotCloudWalSealFailureHandler(
            Failpoint.AfterWalRotation);
        var policy = CreatePolicy(
            128,
            1,
            TimeSpan.FromHours(1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));
        var before = await database.GetRuntimeMetricsAsync();

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "post-rotation-retry",
            PantsWriteOptions.CloudAsync);
        await failpoints.WaitUntilFailureInjectedAsync(AssertionTimeout);
        var published = await WaitForMetricsAsync(
            database,
            candidate =>
                candidate.WalCurrentSegmentId > before.WalCurrentSegmentId &&
                candidate.WalPendingWrites == 0 &&
                candidate.PendingCloudUploads == 0 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);

        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
        Assert.Equal(1, failpoints.Attempts);
        Assert.True(published.WalCloudDurableSequence >= published.CurrentSequence);
    }

    [Fact]
    public async Task ShouldApplyCloudAsyncCommitGivenPostAppendSealIsFenced()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new OneShotCloudWalSealFailureHandler(
            Failpoint.AfterCloudWalSealFlush,
            static () => new PantsFencedException("Injected cloud authority loss."));
        var policy = CreatePolicy(
            128,
            1,
            TimeSpan.FromHours(1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "fenced-after-append",
            PantsWriteOptions.CloudAsync);

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("fenced-after-append"u8.ToArray());
        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        Assert.Equal(1, metrics.WalPendingWrites);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
    }

    [Fact]
    public async Task ShouldApplyCloudStrictCommitGivenPostRotationSealFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new OneShotCloudWalSealFailureHandler(
            Failpoint.AfterWalRotation);
        var policy = CreatePolicy(
            128,
            1,
            TimeSpan.FromHours(1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));

        await Assert.ThrowsAnyAsync<PantsException>(() => CommitAsync(
            database,
            database.DefaultColumnFamily,
            "strict-post-rotation",
            PantsWriteOptions.CloudStrict).AsTask());

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("strict-post-rotation"u8.ToArray());
        var published = await WaitForMetricsAsync(
            database,
            static candidate =>
                candidate.WalPendingWrites == 0 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);

        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        Assert.Equal(1, failpoints.Attempts);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
        Assert.True(published.WalCloudDurableSequence >= published.CurrentSequence);
    }

    [Fact]
    public async Task ShouldKeepFailedCloudStrictUploadOutOfReplacementCache()
    {
        using var directory = new TemporaryDirectory();
        using var replacement = new TemporaryDirectory();
        var failpoints = new RetryingCloudWalUploadFailpointHandler();
        var policy = CreatePolicy(
            128,
            1,
            TimeSpan.FromHours(1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, policy),
            new RuntimeDependencies(failpoints));

        try
        {
            await Assert.ThrowsAnyAsync<PantsException>(() => CommitAsync(
                database,
                database.DefaultColumnFamily,
                "strict-upload-failure",
                PantsWriteOptions.CloudStrict).AsTask());
            using var failureTimeout = new CancellationTokenSource(AssertionTimeout);
            await failpoints.WaitForFailureAsync(failureTimeout.Token);

            await using var reader = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            var value = await reader.GetAsync("strict-upload-failure"u8.ToArray());
            Assert.Equal(
                "value",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(directory.Path, "cloud_store", "wal"),
                "*.wal",
                SearchOption.AllDirectories));

            CopyDirectory(
                Path.Combine(directory.Path, "cloud_store"),
                Path.Combine(replacement.Path, "cloud_store"));
            await using var replacementDatabase = await PantsDatabase.OpenAsync(
                CreateOptions(replacement.Path, policy));
            await using var replacementReader = await replacementDatabase.BeginTransactionAsync(
                replacementDatabase.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Null(await replacementReader.GetAsync("strict-upload-failure"u8.ToArray()));
        }
        finally
        {
            failpoints.AllowSuccess();
        }

        var published = await WaitForMetricsAsync(
            database,
            static candidate =>
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);
        Assert.True(published.WalCloudDurableSequence >= published.CurrentSequence);
    }

    [Fact]
    public async Task ShouldEventuallyPublishSstGivenManyCloudBufferedWritesWhenMemtableNeverReachesSizeThreshold()
    {
        using var directory = new TemporaryDirectory();
        var policy = CreatePolicy(4, 1);
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
        var policy = CreatePolicy(4, 10_000);
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
        var policy = CreatePolicy(4, 1);
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
        var policy = CreatePolicy(128, 1);
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
        transaction.Put(Encoding.UTF8.GetBytes(key), "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(AssertionTimeout);
        PantsRuntimeMetrics? last = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var metrics = await database.GetRuntimeMetricsAsync(timeout.Token);
                last = metrics;
                if (predicate(metrics))
                {
                    return metrics;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                $"Cloud metrics did not converge: sequence={last?.CurrentSequence}, " +
                $"segment={last?.WalCurrentSegmentId}, pending={last?.WalPendingWrites}, " +
                $"cloud={last?.WalCloudDurableSequence}, uploads={last?.PendingCloudUploads}, " +
                $"health={last?.Health}.",
                exception);
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    static PantsCloudWritePolicy CreatePolicy(
        long segmentGap,
        int maximumPendingWrites,
        TimeSpan? maximumDelay = null) => new(
        segmentGap,
        long.MaxValue,
        maximumDelay ?? TimeSpan.FromHours(1),
        maximumPendingWrites);

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        PantsCloudWritePolicy policy) =>
        PantsDatabase.OpenAsync(CreateOptions(path, policy));

    static PantsOpenOptions CreateOptions(string path, PantsCloudWritePolicy policy) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "eventual-flush/")
            .WithCloudWritePolicy(policy)
            .WithBackgroundCompaction(false);
}
