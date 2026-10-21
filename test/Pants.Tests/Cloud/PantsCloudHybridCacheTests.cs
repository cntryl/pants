using System.Globalization;
using System.Net;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudHybridCacheTests
{
    const long LocalCacheBudgetBytes = 2200 * 1024;
    const long RecoveryLocalBudgetBytes = 128 * 1024;
    const int RecoveryCommitCount = 40;
    const int RecoveryValueBytes = 8 * 1024;

    [Fact]
    public async Task ShouldEvictProvenSstsAndAcceptWritesWhileProviderGarbageCollectionIsUnavailable()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path)
                .WithCloudLocalStorageBudget(LocalCacheBudgetBytes),
            new RuntimeDependencies(cloudHttpClient: client));
        Assert.Equal(
            LocalCacheBudgetBytes,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridMaximumLocalBytes);
        handler.FailSstList = true;

        for (var index = 0; index < 10; index++)
        {
            await PutAndFlushAsync(database, $"residue-{index}", index);
        }

        Assert.True(LocalSsts(cache.Path).Length < 10);
        Assert.True(handler.GetObjectPaths("/sst/").Count(static path => path.EndsWith(
            ".sst",
            StringComparison.Ordinal)) >= 10);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.HybridUsagePercent < 90);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);

        await PutAndFlushAsync(database, "write-after-residue", 7);
        Assert.True(LocalSsts(cache.Path).Length < 11);
        await AssertReadableAsync(database, "residue-5");
        await AssertReadableAsync(database, "write-after-residue");
    }

    [Fact]
    public async Task ShouldEvictProviderSstsAfterCloudAsyncBackgroundFlush()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var writePolicy = new PantsCloudWritePolicy(
            EventualFlushSegmentGap: 1,
            WalSealMinimumSegmentBytes: long.MaxValue,
            WalSealMaximumFlushDelay: TimeSpan.FromMilliseconds(10),
            WalSealMaximumPendingWrites: int.MaxValue);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path)
                .WithCloudLocalStorageBudget(LocalCacheBudgetBytes)
                .WithCompaction(new PantsCompactionConfiguration(
                    L0FileCountTrigger: 32,
                    BackgroundEnabled: false))
                .WithCloudWritePolicy(writePolicy),
            new RuntimeDependencies(cloudHttpClient: client));

        var value = new byte[128 * 1024];
        for (var index = 0; index < 20; index++)
        {
            new Random(index).NextBytes(value);
            await CommitAsync(database, $"cloud-async-{index}", value, PantsWriteOptions.CloudAsync);
        }

        var metrics = await WaitForMetricsAsync(
            database,
            static candidate => candidate.SstCount >= 9 && candidate.HybridUsagePercent < 90);

        Assert.True(LocalSsts(cache.Path).Length < metrics.SstCount);
        Assert.True(handler.GetObjectPaths("/sst/").Count(static path => path.EndsWith(
            ".sst",
            StringComparison.Ordinal)) >= metrics.SstCount);
        await AssertReadableAsync(database, "cloud-async-19", value.Length);
    }

    [Fact]
    public async Task ShouldKeepPeakLocalBytesWithinBudgetWhileReplayingProviderWalBacklog()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var sealPolicy = new PantsCloudWritePolicy(
            EventualFlushSegmentGap: int.MaxValue,
            WalSealMinimumSegmentBytes: 1,
            WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
            WalSealMaximumPendingWrites: 1);
        var seedOptions = CreateOptions(cache.Path)
            .WithMemtableLimits(8 * 1024 * 1024)
            .WithCloudWritePolicy(sealPolicy);
        var recoveryOptions = seedOptions.WithCloudLocalStorageBudget(RecoveryLocalBudgetBytes);
        await using (var seed = await PantsDatabase.OpenForTestingAsync(
                         seedOptions,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            for (var index = 0; index < RecoveryCommitCount; index++)
            {
                await using var transaction = await seed.Transactions.BeginAsync(
                    seed.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(RecoveryKey(index), RecoveryValue(index));
                await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
            }

            _ = await WaitForMetricsAsync(
                seed,
                static metrics =>
                    metrics.CurrentSequence >= RecoveryCommitCount &&
                    metrics.WalCloudDurableSequence >= metrics.CurrentSequence);
            Assert.Equal(0, handler.SstStoredBytes);
            Assert.True(handler.WalStoredBytes > RecoveryLocalBudgetBytes);
            handler.AcknowledgeSstWritesWithoutPersisting = true;
            await seed.ShutdownAsync(TestTimeouts.Expected);
            handler.AcknowledgeSstWritesWithoutPersisting = false;
            Assert.Equal(0, handler.SstStoredBytes);
            Assert.True(handler.WalStoredBytes > 0);
        }

        ClearLocalCache(cache.Path);
        var sampler = new RecoveryLocalBytesSampler(cache.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            recoveryOptions,
            new RuntimeDependencies(sampler, cloudHttpClient: client));

        var recovered = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(PantsEngineHealth.Healthy, recovered.Health);
        Assert.Equal(RecoveryLocalBudgetBytes, recovered.HybridMaximumLocalBytes);
        Assert.True(sampler.Samples.Count > 0, "Expected local-storage samples during WAL replay.");
        Assert.All(sampler.Samples, sample => Assert.InRange(sample, 0, RecoveryLocalBudgetBytes));
        Assert.True(handler.SstStoredBytes > 0, "Replay checkpoints should be published to cloud SST storage.");
        Assert.Empty(LocalSsts(cache.Path));

        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < RecoveryCommitCount; index++)
        {
            Assert.Equal(
                RecoveryValue(index),
                (await reader.GetAsync(RecoveryKey(index)))?.ToArray());
        }
    }

    [Fact]
    public async Task ShouldRecoverProviderCloudAsyncWritesAfterShutdownCheckpointAndCacheLoss()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var sealPolicy = new PantsCloudWritePolicy(
            EventualFlushSegmentGap: int.MaxValue,
            WalSealMinimumSegmentBytes: 1,
            WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
            WalSealMaximumPendingWrites: 1);
        var seedOptions = CreateOptions(cache.Path)
            .WithMemtableLimits(8 * 1024 * 1024)
            .WithCloudWritePolicy(sealPolicy);
        var recoveryOptions = seedOptions.WithCloudLocalStorageBudget(RecoveryLocalBudgetBytes);
        await using (var seed = await PantsDatabase.OpenForTestingAsync(
                         seedOptions,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            for (var index = 0; index < RecoveryCommitCount; index++)
            {
                await using var transaction = await seed.Transactions.BeginAsync(
                    seed.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(RecoveryKey(index), RecoveryValue(index));
                await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
            }

            _ = await WaitForMetricsAsync(
                seed,
                static metrics =>
                    metrics.CurrentSequence >= RecoveryCommitCount &&
                    metrics.WalCloudDurableSequence >= metrics.CurrentSequence);
            Assert.Equal(0, handler.SstStoredBytes);
            Assert.True(handler.WalStoredBytes > RecoveryLocalBudgetBytes);
            await seed.ShutdownAsync(TestTimeouts.Expected);
            Assert.True(handler.SstStoredBytes > 0);
        }

        ClearLocalCache(cache.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            recoveryOptions,
            new RuntimeDependencies(cloudHttpClient: client));
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var count = 0;
        await foreach (var row in scan)
        {
            count++;
            var index = int.Parse(
                TestBytes.ToText(row.Key).AsSpan("recovery-backlog:".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            Assert.Equal(RecoveryValue(index), row.Value.ToArray());
        }

        Assert.Equal(RecoveryCommitCount, count);
    }

    [Fact]
    public async Task ShouldRetainLocalSstAfterProviderReadbackMismatchAndRetryMirror()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var corruptingHandler = new CorruptNextSstRangeHandler(handler);
        using var client = new HttpClient(corruptingHandler, false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path).WithCloudLocalStorageBudget(LocalCacheBudgetBytes),
            new RuntimeDependencies(cloudHttpClient: client));

        corruptingHandler.CorruptNextSstRangeExcept(handler.GetObjectPaths("/sst/"));
        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PutAndFlushAsync(database, "provider-failed-readback", 11));

        var localSst = Assert.Single(LocalSsts(cache.Path));
        var remoteSsts = handler.GetObjectPaths("/sst/")
            .Where(static path => path.EndsWith(".sst", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(corruptingHandler.CorruptedObjectPath, remoteSsts);
        Assert.True(File.Exists(localSst));

        await CommitEmptyCloudStrictAsync(database);

        Assert.Single(LocalSsts(cache.Path));
        await AssertReadableAsync(database, "provider-failed-readback");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveProviderCloudLocalStorageBudget(long bytes)
    {
        using var cache = new TemporaryDirectory();

        var exception = Assert.Throws<PantsInvalidArgumentException>(() =>
            CreateOptions(cache.Path).WithCloudLocalStorageBudget(bytes));

        Assert.Contains("greater than zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Cloud(
                path,
                new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider(
                        "account",
                        "container",
                        new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")),
                    "database"))
            .WithBackgroundCompaction(false);

    static string[] LocalSsts(string path)
    {
        var directory = Path.Combine(path, "sst");
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.sst")
            : [];
    }

    static byte[] RecoveryKey(int index) => TestBytes.FromString($"recovery-backlog:{index:D3}");

    static byte[] RecoveryValue(int index)
    {
        var value = new byte[RecoveryValueBytes];
        new Random(index + 37).NextBytes(value);
        return value;
    }

    static void ClearLocalCache(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    static async Task PutAndFlushAsync(IPantsDatabase database, string key, int seed)
    {
        var value = new byte[256 * 1024];
        new Random(seed).NextBytes(value);
        await CommitAsync(database, key, value);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static Task CommitAsync(IPantsDatabase database, string key, int seed)
    {
        var value = new byte[256 * 1024];
        new Random(seed).NextBytes(value);
        return CommitAsync(database, key, value);
    }

    static async Task CommitAsync(IPantsDatabase database, string key, byte[] value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        string key,
        byte[] value,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(writeOptions);
    }

    static async Task CommitEmptyCloudStrictAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task AssertReadableAsync(
        IPantsDatabase database,
        string key,
        int expectedLength = 256 * 1024)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(expectedLength, Assert.IsType<ReadOnlyMemory<byte>>(
            await transaction.GetAsync(TestBytes.FromString(key))).Length);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        PantsRuntimeMetrics? last = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var metrics = await database.Diagnostics.GetRuntimeMetricsAsync(timeout.Token);
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
                $"Hybrid metrics did not converge: sequence={last?.CurrentSequence}, " +
                $"ssts={last?.SstCount}, usage={last?.HybridUsagePercent}, " +
                $"health={last?.Health}.",
                exception);
        }
    }

    sealed class CorruptNextSstRangeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        string[] _alreadyPublished = [];
        string? _corruptedObjectPath;
        int _armed;

        public string? CorruptedObjectPath => Volatile.Read(ref _corruptedObjectPath);

        public void CorruptNextSstRangeExcept(string[] alreadyPublished)
        {
            Volatile.Write(ref _alreadyPublished, alreadyPublished);
            Volatile.Write(ref _armed, 1);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method != HttpMethod.Get ||
                request.Headers.Range is null ||
                request.RequestUri?.AbsolutePath.Contains("/sst/", StringComparison.Ordinal) != true ||
                Volatile.Read(ref _alreadyPublished).Contains(
                    request.RequestUri.AbsolutePath,
                    StringComparer.Ordinal) ||
                response.StatusCode != HttpStatusCode.PartialContent ||
                Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return response;
            }

            var original = response.Content;
            var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return response;
            }

            bytes[0] ^= 0xFF;
            Volatile.Write(ref _corruptedObjectPath, request.RequestUri.AbsolutePath);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in original.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = replacement;
            original.Dispose();
            return response;
        }
    }

    sealed class RecoveryLocalBytesSampler(string databasePath) : IFailpointHandler
    {
        readonly Lock _gate = new();
        readonly List<long> _samples = [];

        public IReadOnlyList<long> Samples
        {
            get
            {
                lock (_gate)
                {
                    return [.. _samples];
                }
            }
        }

        public void Hit(Failpoint failpoint)
        {
            _ = failpoint;
            var bytes = Directory.GetFiles(databasePath, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            lock (_gate)
            {
                _samples.Add(bytes);
            }
        }
    }

    sealed class FailOnceAtFailpointHandler(Failpoint target) : IFailpointHandler
    {
        int _hits;

        public int Hits => Volatile.Read(ref _hits);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Increment(ref _hits) == 1)
            {
                throw new PantsIOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
