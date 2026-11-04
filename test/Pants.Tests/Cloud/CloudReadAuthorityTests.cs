using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudReadAuthorityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldDiscardRemoteReadCompletionWhenCloudLeaseExpiresInFlight(bool scanRead, bool failRead)
    {
        using var seedCache = new TemporaryDirectory();
        using var coldCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new SstRangeHoldHandler(handler, failRead);
        using var client = new HttpClient(hold);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dependencies = new RuntimeDependencies(cloudHttpClient: client,
            leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            runtimeTimeProvider: monotonic, leaseClock: wallClock);
        PantsOpenOptions Options(string path) => PantsOpenOptions.Cloud(path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30));
        await using (var seed = await PantsDatabase.OpenForTestingAsync(Options(seedCache.Path), dependencies))
        {
            await using var writer = await seed.Transactions.BeginAsync(
                seed.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await seed.Maintenance.FlushAsync(seed.ColumnFamilies.DefaultFamily);
        }

        await using var database = await PantsDatabase.OpenForTestingAsync(Options(coldCache.Path), dependencies);
        Assert.Empty(Directory.GetFiles(Path.Combine(coldCache.Path, "sst"), "*.sst"));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        await using var iterator = scan.GetAsyncEnumerator();
        hold.Arm();
        Task read = scanRead ? iterator.MoveNextAsync().AsTask() : reader.GetAsync("key"u8.ToArray()).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        handler.BlockLeaseWrites();
        try
        {
            monotonic.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            monotonic.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1));
            Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
            hold.Release();

            await Assert.ThrowsAsync<PantsFencedException>(() => read.WaitAsync(TestTimeouts.Expected));
        }
        finally
        {
            hold.Release();
            handler.ReleaseLeaseWrites();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFenceRetainedCloudReadsAndScansAfterMonotonicLeaseExpiry(bool flushed)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30));
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic, leaseClock: wallClock));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            writer.Put("key/first"u8.ToArray(), "value/first"u8.ToArray());
            writer.Put("key/second"u8.ToArray(), "value/second"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        if (flushed)
        {
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await using var retained = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await retained.ScanAsync(new PantsScanQuery());
        await using var iterator = scan.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());

        handler.BlockLeaseWrites();
        try
        {
            monotonic.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            monotonic.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1));
            Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);

            await Assert.ThrowsAsync<PantsFencedException>(() =>
                retained.GetAsync("key/first"u8.ToArray()).AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() =>
                retained.GetWithDiagnosticsAsync("key/first"u8.ToArray()).AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() =>
                retained.ScanAsync(new PantsScanQuery()).AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() => iterator.MoveNextAsync().AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() =>
                database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadOnly).AsTask());

            // Lease loss keeps the running runtime's diagnostic control path available.
            Assert.NotNull(await database.Diagnostics.GetRuntimeMetricsAsync());
        }
        finally
        {
            handler.ReleaseLeaseWrites();
        }
    }

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")), "database");

    sealed class SstRangeHoldHandler(HttpMessageHandler inner, bool failRead) : DelegatingHandler(inner)
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.Headers.Range is not null &&
                request.RequestUri!.AbsolutePath.Contains("/sst/", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                if (failRead)
                {
                    throw new HttpRequestException("Arranged provider read failure after lease expiry.");
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
