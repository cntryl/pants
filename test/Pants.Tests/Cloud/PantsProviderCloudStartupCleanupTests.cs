using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Abstractions;

namespace Cntryl.Pants.Cloud;

public sealed class PantsProviderCloudStartupCleanupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldApplyOneRuntimeResponseDeadlineAcrossCloudStartup(bool expireDuringServiceStartup)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var runtimeResponseTimeout = TimeSpan.FromSeconds(30);
        var timeProvider = new ManualTimeProvider();
        var deadlineAdvanced = 0;
        var expiredPhase = expireDuringServiceStartup
            ? StartupPhase.ServiceStartup
            : StartupPhase.ManifestSnapshot;
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithStorageTimeout(TimeSpan.FromSeconds(5))
            .WithRuntimeResponseTimeout(runtimeResponseTimeout)
            .WithLeaseTimeToLive(TimeSpan.FromMinutes(2))
            .WithBackgroundCompaction(false);
        var dependencies = new RuntimeDependencies(
            cloudHttpClient: client,
            runtimeTimeProvider: timeProvider,
            startupPhaseMeasurement: measurement =>
            {
                if (measurement.Phase == expiredPhase &&
                    Interlocked.Exchange(ref deadlineAdvanced, 1) == 0)
                {
                    timeProvider.Advance(runtimeResponseTimeout);
                }
            });

        await Assert.ThrowsAsync<PantsTimeoutException>(() => PantsDatabase.OpenForTestingAsync(
            options,
            dependencies).AsTask());

        await using var recovered = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        Assert.True(recovered.Cloud!.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldReportColdStartReadinessAndMaintenancePeakForGrowingRemoteHistory()
    {
        const long ProductionMemoryProfileBytes = 512L * 1024 * 1024;
        const long LocalDiskProfileBudgetBytes = 64L * 1024 * 1024;
        const int HistoryWriteCount = 128;
        const int MinimumHistorySstCount = 16;
        var storageTimeout = TimeSpan.FromSeconds(5);
        var runtimeResponseTimeout = TimeSpan.FromSeconds(30);
        using var sourceCache = new TemporaryDirectory();
        using var coldCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateLocation();

        var sourceOptions = PantsOpenOptions.Cloud(sourceCache.Path, location)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.Mixed)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(ProductionMemoryProfileBytes))
            .WithCloudLocalStorageBudget(LocalDiskProfileBudgetBytes)
            .WithBackgroundCompaction(false);
        await using (var source = await PantsDatabase.OpenForTestingAsync(
                         sourceOptions,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            for (var index = 0; index < HistoryWriteCount; index++)
            {
                await using var transaction = await source.Transactions.BeginAsync(
                    source.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"history-key-{index:D2}"),
                    HistoryValue(index));
                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
                await source.Maintenance.FlushAsync(source.ColumnFamilies.DefaultFamily);
            }
        }

        var sstCount = handler.GetObjectPaths("/sst/")
            .Count(static path => path.EndsWith(".sst", StringComparison.Ordinal));
        Assert.True(
            sstCount >= MinimumHistorySstCount,
            $"Expected at least {MinimumHistorySstCount} remote SSTs, found {sstCount}.");
        Assert.True(handler.SstStoredBytes > 0);

        var measurements = new List<StartupPhaseMeasurement>();
        var options = PantsOpenOptions.Cloud(coldCache.Path, location)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.Mixed)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(ProductionMemoryProfileBytes))
            .WithCloudLocalStorageBudget(LocalDiskProfileBudgetBytes)
            .WithStorageTimeout(storageTimeout)
            .WithRuntimeResponseTimeout(runtimeResponseTimeout)
            .WithBackgroundCompaction(false);
        var dependencies = new RuntimeDependencies(
            cloudHttpClient: client,
            startupPhaseMeasurement: measurements.Add);
        var resolved = PantsOpenOptionsResolver.Resolve(options);

        handler.ResetSstReadMetrics();
        using var process = Process.GetCurrentProcess();
        await using var localCacheSampler = new DirectoryByteSampler(coldCache.Path);
        process.Refresh();
        var processCpuBeforeOpen = process.TotalProcessorTime;
        var coldOpen = Stopwatch.StartNew();
        await using var database = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        coldOpen.Stop();
        process.Refresh();
        var processCpuDuringOpen = process.TotalProcessorTime - processCpuBeforeOpen;
        await localCacheSampler.StopAsync();
        var sampledPeakLocalCacheBytes = localCacheSampler.PeakBytes;
        var coldStartSstFullReads = handler.SstFullReads;
        var coldStartSstMetadataReads = handler.SstMetadataReads;
        var coldStartSstRangeReads = handler.SstRangeReads;
        var coldStartSstRangeBytes = handler.SstRangeBytes;
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(ProductionMemoryProfileBytes, resolved.MemoryBudgetBytes);
        Assert.Equal(runtimeResponseTimeout, resolved.RuntimeResponseTimeout);
        Assert.Equal(ProductionMemoryProfileBytes, metrics.MemoryBudgetBytes);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        Assert.True(database.Cloud!.IsPrimaryLeaseHealthy);
        Assert.True(coldOpen.Elapsed <= runtimeResponseTimeout);
        Assert.Equal(LocalDiskProfileBudgetBytes, metrics.HybridMaximumLocalBytes);
        Assert.InRange(
            metrics.HybridTotalCommittedBytes,
            0,
            LocalDiskProfileBudgetBytes);
        Assert.InRange(sampledPeakLocalCacheBytes, 1, LocalDiskProfileBudgetBytes);
        Assert.Contains(StartupPhase.CloudControlHydration, measurements.Select(static item => item.Phase));
        Assert.Contains(StartupPhase.WalReplay, measurements.Select(static item => item.Phase));
        Assert.Contains(StartupPhase.ServiceStartup, measurements.Select(static item => item.Phase));
        Assert.Equal(sstCount, coldStartSstMetadataReads);
        Assert.Equal(0, coldStartSstFullReads);
        Assert.Equal(0, coldStartSstRangeReads);
        Assert.Equal(0L, coldStartSstRangeBytes);
        Assert.Equal(resolved.CompactionMemoryPoolBytes, metrics.MaintenanceMemoryCapacityBytes);
        Assert.InRange(
            metrics.MaintenanceMemoryPeakBytes,
            1,
            metrics.MaintenanceMemoryCapacityBytes);
        Assert.InRange(
            metrics.MaintenanceMemoryUsedBytes,
            0,
            metrics.MaintenanceMemoryCapacityBytes);
        Assert.Equal(metrics.CompactionBufferCapacityBytes, metrics.MaintenanceMemoryCapacityBytes);

        await using (var reader = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            for (var index = 0; index < HistoryWriteCount; index++)
            {
                Assert.Equal(
                    HistoryValue(index),
                    (await reader.GetAsync(TestBytes.FromString($"history-key-{index:D2}")))?.ToArray());
            }
        }

        output.WriteLine(
            $"512 MiB memory / 64 MiB local-disk profile; {sstCount} remote SSTs, " +
            $"{handler.SstStoredBytes} remote SST bytes; " +
            $"cold open {coldOpen.Elapsed.TotalMilliseconds:F1} ms; maintenance peak " +
            $"{metrics.MaintenanceMemoryPeakBytes}/{metrics.MaintenanceMemoryCapacityBytes} bytes; " +
            $"local cache committed {metrics.HybridTotalCommittedBytes}/{metrics.HybridMaximumLocalBytes} bytes, " +
            $"sampled directory peak {sampledPeakLocalCacheBytes} bytes; " +
            $"process CPU during open {processCpuDuringOpen.TotalMilliseconds:F1} ms; " +
            $"cold-start SST reads: {coldStartSstMetadataReads} metadata HEADs, " +
            $"{coldStartSstFullReads} full, {coldStartSstRangeReads} ranges / " +
            $"{coldStartSstRangeBytes} bytes; " +
            $"runtime response timeout {runtimeResponseTimeout.TotalSeconds:F0} s.");
        foreach (var measurement in measurements)
        {
            output.WriteLine(
                $"{measurement.Phase}: {measurement.Elapsed.TotalMilliseconds:F1} ms, " +
                $"{measurement.AllocatedBytes} B allocated");
        }
    }

    static byte[] HistoryValue(int index)
    {
        var value = new byte[4 * 1024];
        new Random(index).NextBytes(value);
        return value;
    }

    sealed class DirectoryByteSampler : IAsyncDisposable
    {
        static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(1);
        readonly string _rootPath;
        readonly CancellationTokenSource _cancellation = new();
        readonly Task _samplingTask;
        int _stopped;
        long _peakBytes;

        public DirectoryByteSampler(string rootPath)
        {
            _rootPath = rootPath;
            Sample();
            _samplingTask = SampleUntilCanceledAsync();
        }

        public long PeakBytes => Volatile.Read(ref _peakBytes);

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _cancellation.Cancel();
                try
                {
                    try
                    {
                        await _samplingTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        // Stopping the sampler is its normal completion path.
                    }

                    Sample();
                }
                finally
                {
                    _cancellation.Dispose();
                }
            }
        }

        public ValueTask DisposeAsync() => StopAsync();

        async Task SampleUntilCanceledAsync()
        {
            using var timer = new PeriodicTimer(SamplingInterval);
            while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
            {
                Sample();
            }
        }

        void Sample()
        {
            var bytes = MeasureDirectoryBytes(_rootPath);
            var observedPeak = Volatile.Read(ref _peakBytes);
            while (bytes > observedPeak)
            {
                var priorPeak = Interlocked.CompareExchange(ref _peakBytes, bytes, observedPeak);
                if (priorPeak == observedPeak)
                {
                    return;
                }

                observedPeak = priorPeak;
            }
        }

        static long MeasureDirectoryBytes(string rootPath)
        {
            var total = 0L;
            foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total = checked(total + new FileInfo(path).Length);
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Startup cleanup can remove a file between enumeration and measurement.
                }
            }

            return total;
        }
    }

    [Fact]
    public async Task ShouldReleaseCloudLeaseAfterStrictHydrationFailure()
    {
        using var initialCache = new TemporaryDirectory();
        using var recoveryCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = CreateLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var database = await OpenAsync(
                         initialCache.Path,
                         location,
                         dependencies))
        {
            await CommitCloudStrictAsync(database);
        }

        var remoteWal = Assert.Single(handler.GetObjectPaths("/wal/epochs/"));
        handler.ReplaceObjectText(remoteWal, "corrupt");
        var recoveryOptions = PantsOpenOptions.Cloud(recoveryCache.Path, location)
            .WithRecoveryPolicy(PantsRecoveryPolicy.Strict)
            .WithBackgroundCompaction(false);

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => PantsDatabase.OpenForTestingAsync(
            recoveryOptions,
            dependencies).AsTask());

        await using var salvaged = await PantsDatabase.OpenForTestingAsync(
            recoveryOptions.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage),
            dependencies);
        Assert.Equal(
            PantsEngineHealth.SalvageMode,
            (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldReleaseCloudAndLocalLeasesAfterWalCatalogFencingFailure()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler
        {
            AcknowledgeWalCatalogWritesWithoutPersisting = true
        };
        using var client = new HttpClient(handler);
        var location = CreateLocation();
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, location)
            .WithBackgroundCompaction(false);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask());

        handler.AcknowledgeWalCatalogWritesWithoutPersisting = false;
        await using var corrected = await PantsDatabase.OpenForTestingAsync(
            options,
            dependencies);
        Assert.True(corrected.Cloud!.IsPrimaryLeaseHealthy);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string cachePath,
        PantsCloudStorageLocation location,
        RuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cachePath, location)
                .WithBackgroundCompaction(false),
            dependencies);

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "startup-cleanup");

    static async ValueTask CommitCloudStrictAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }
}
