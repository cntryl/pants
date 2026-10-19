using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

/// <summary>Cloud WAL retirement as background maintenance of a provider-backed engine.</summary>
public sealed class PantsCloudWalRetirementTests
{
    [Fact]
    public async Task ShouldKeepCloudLeaseRenewingUntilBlockedWorkerExitsAfterCancellationFailure()
    {
        using var cache = new TemporaryDirectory();
        using var competingCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var leaseTimeToLive = TimeSpan.FromMilliseconds(500);
        var database = await OpenAsync(
            cache.Path,
            client,
            workerDisposalTimeout: TimeSpan.FromMilliseconds(300),
            leaseTimeToLive: leaseTimeToLive);
        try
        {
            await CommitAsync(database, "lease-held-through-worker");
            handler.BlockWalRangeReadsThroughCancellationFailure();
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await handler.WaitForBlockedWalRangeReadAsync();

            var firstShutdown = database.ShutdownAsync(TestTimeouts.Expected).AsTask();
            var secondShutdown = database.ShutdownAsync(TestTimeouts.Expected).AsTask();
            var shutdownFailure = await Assert.ThrowsAsync<PantsTimeoutException>(() => firstShutdown);
            var concurrentShutdownFailure = await Assert.ThrowsAsync<PantsTimeoutException>(
                () => secondShutdown);
            Assert.Equal(PantsErrorCode.Timeout, shutdownFailure.Code);
            Assert.Equal(PantsErrorCode.Timeout, concurrentShutdownFailure.Code);

            // The cleanup deadline has elapsed, but the retirement worker is still inside the
            // provider. The heartbeat and remote lease must remain live for its full lifetime.
            await Task.Delay(TimeSpan.FromMilliseconds(900));
            IPantsDatabase? competingDatabase = null;
            try
            {
                competingDatabase = await OpenAsync(
                    competingCache.Path,
                    client,
                    leaseTimeToLive: leaseTimeToLive);
            }
            catch (PantsLeaseHeldException)
            {
            }

            if (competingDatabase is not null)
            {
                await competingDatabase.DisposeAsync();
            }

            Assert.Null(competingDatabase);
        }
        finally
        {
            handler.ReleaseWalRangeReads();
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception)
            {
                // The test observes the first shutdown failure; release unblocks deferred cleanup.
            }
        }

        await using var recovered = await OpenUntilCloudLeaseReleasedAsync(
            competingCache.Path,
            client,
            leaseTimeToLive);
        await AssertValueAsync(recovered, "lease-held-through-worker");
    }

    [Fact]
    public async Task ShouldNotReportShutdownCompleteWhileDeferredWorkerCleanupStillOwnsCloudLease()
    {
        using var cache = new TemporaryDirectory();
        using var successorCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var database = await OpenAsync(
            cache.Path,
            client,
            workerDisposalTimeout: TimeSpan.FromMilliseconds(300),
            leaseTimeToLive: TimeSpan.FromMilliseconds(500));
        try
        {
            await CommitAsync(database, "lease-held-through-retry");
            handler.BlockWalRangeReadsThroughCancellationFailure();
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await handler.WaitForBlockedWalRangeReadAsync();

            var firstFailure = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            Assert.Equal(PantsErrorCode.Timeout, firstFailure.Code);

            var retryFailure = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(100)).AsTask());
            Assert.Equal(PantsErrorCode.Timeout, retryFailure.Code);

            // The retry must keep waiting for the worker that owns the cloud lease. A completed
            // shutdown result here would claim disposal succeeded while deferred cleanup is live.
            IPantsDatabase? competingDatabase = null;
            try
            {
                competingDatabase = await OpenAsync(
                    successorCache.Path,
                    client,
                    leaseTimeToLive: TimeSpan.FromMilliseconds(500));
            }
            catch (PantsLeaseHeldException)
            {
            }

            if (competingDatabase is not null)
            {
                await competingDatabase.DisposeAsync();
            }

            Assert.Null(competingDatabase);
        }
        finally
        {
            handler.ReleaseWalRangeReads();
            try
            {
                await database.ShutdownAsync(TestTimeouts.Expected);
            }
            catch (Exception)
            {
                // Release unblocks deferred cleanup; the test reports the observed shutdown result.
            }
        }

        await using var recovered = await OpenUntilCloudLeaseReleasedAsync(
            successorCache.Path,
            client,
            TimeSpan.FromMilliseconds(500));
        await AssertValueAsync(recovered, "lease-held-through-retry");
    }

    [Fact]
    public async Task ShouldNotDelayCloudStrictCommitWhileWalRetirementWaitsOnProvider()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenAsync(cache.Path, client);
        await CommitAsync(database, "flushed");
        handler.BlockWalRangeReads();
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await handler.WaitForBlockedWalRangeReadAsync();

        // Retirement is stuck inside a provider read; a CloudStrict commit must not wait for it.
        await CommitAsync(database, "committed-during-retirement").WaitAsync(TestTimeouts.Expected);

        Assert.Equal(2, ReadCatalogSegmentCount(handler));
        using (var catalog = JsonDocument.Parse(
                   handler.GetObjectText("/wal/publication-catalog.v1.json")))
        {
            var root = catalog.RootElement;
            Assert.Equal(3UL, root.GetProperty("wal_history_start_segment_id").GetUInt64());
            Assert.Equal(4UL, root.GetProperty("next_segment_id").GetUInt64());
            Assert.Equal(
                ["1", "3"],
                root.GetProperty("segments").EnumerateObject()
                    .Select(static segment => segment.Name)
                    .ToArray());
        }

        handler.ReleaseWalRangeReads();
        await TestWait.UntilAsync(
            () => ReadCatalogSegmentCount(handler) == 1,
            "the flushed WAL segment retires once the provider answers");
        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        await AssertValueAsync(database, "committed-during-retirement");
    }

    [Fact]
    public async Task ShouldRetireWalAcrossProviderTimeoutsWithoutDegradingHealth()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await OpenAsync(cache.Path, client);
        await CommitAsync(database, "slow-provider");
        // Two attempts' worth of reads time out, each after the provider's own bounded retries.
        handler.TimeOutNextWalRangeReads(2 * CloudRetryPolicy.MaximumAttempts);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await TestWait.UntilAsync(
            () => !handler.ContainsObjectPath("/wal/epochs/"),
            "retirement finishes on a later attempt");

        Assert.Equal(2 * CloudRetryPolicy.MaximumAttempts, handler.FailedWalRangeReads);
        Assert.Equal(0, ReadCatalogSegmentCount(handler));
        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldResumeInterruptedWalRetirementAfterReopenAndRecoverFromCloud()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using (var database = await OpenAsync(cache.Path, client))
        {
            await CommitAsync(database, "interrupted");
            handler.BlockWalRangeReads();
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await handler.WaitForBlockedWalRangeReadAsync();
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        // The interrupted turn left the catalog untouched.
        Assert.Equal(1, ReadCatalogSegmentCount(handler));
        Assert.True(handler.ContainsObjectPath("/wal/epochs/"));
        handler.ReleaseWalRangeReads();
        Directory.Delete(cache.Path, true);
        Directory.CreateDirectory(cache.Path);

        await using var reopened = await OpenAsync(cache.Path, client);
        await TestWait.UntilAsync(
            () => !handler.ContainsObjectPath("/wal/epochs/"),
            "the reopened engine finishes retirement");

        Assert.Equal(0, ReadCatalogSegmentCount(handler));
        await AssertValueAsync(reopened, "interrupted");
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string root,
        HttpClient client,
        TimeSpan? workerDisposalTimeout = null,
        TimeSpan? leaseTimeToLive = null) =>
        PantsDatabase.OpenForTestingAsync(
            CreateOptions(root, leaseTimeToLive),
            new RuntimeDependencies(
                cloudHttpClient: client,
                workerDisposalTimeout: workerDisposalTimeout));

    static async ValueTask<IPantsDatabase> OpenUntilCloudLeaseReleasedAsync(
        string root,
        HttpClient client,
        TimeSpan leaseTimeToLive)
    {
        using var deadline = new CancellationTokenSource(TestTimeouts.Expected);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                return await OpenAsync(root, client, leaseTimeToLive: leaseTimeToLive);
            }
            catch (PantsLeaseHeldException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
        }
    }

    static PantsOpenOptions CreateOptions(string root, TimeSpan? leaseTimeToLive)
    {
        var options = PantsOpenOptions.Cloud(
                root,
                new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider(
                        "account",
                        "container",
                        new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")),
                    "database"))
            .WithBackgroundCompaction(false);
        return leaseTimeToLive is { } timeToLive
            ? options
                .WithLeaseClockSkewTolerance(TimeSpan.Zero)
                .WithLeaseTimeToLive(timeToLive)
            : options;
    }

    static async Task CommitAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async Task AssertValueAsync(IPantsDatabase database, string key)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync(TestBytes.FromString(key)))));
    }

    static int ReadCatalogSegmentCount(InMemoryAzureBlobHandler handler)
    {
        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        return catalog.RootElement.GetProperty("segments").EnumerateObject().Count();
    }
}
