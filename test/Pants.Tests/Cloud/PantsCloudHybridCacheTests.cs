using System.Net;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudHybridCacheTests
{
    const long LocalCacheBudgetBytes = 560 * 1024;

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

        for (var index = 0; index < 6; index++)
        {
            await PutAndFlushAsync(database, $"residue-{index}", index);
        }

        Assert.Single(LocalSsts(cache.Path));
        Assert.True(handler.GetObjectPaths("/sst/").Count(static path => path.EndsWith(
            ".sst",
            StringComparison.Ordinal)) >= 6);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.HybridUsagePercent < 90);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);

        await PutAndFlushAsync(database, "write-after-residue", 7);
        Assert.Single(LocalSsts(cache.Path));
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
                .WithCloudWritePolicy(writePolicy),
            new RuntimeDependencies(cloudHttpClient: client));

        var value = new byte[128 * 1024];
        for (var index = 0; index < 10; index++)
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
        await AssertReadableAsync(database, "cloud-async-9", value.Length);
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
}
