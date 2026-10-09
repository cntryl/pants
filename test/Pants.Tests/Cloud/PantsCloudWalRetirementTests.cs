using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

/// <summary>Cloud WAL retirement as background maintenance of a provider-backed engine.</summary>
public sealed class PantsCloudWalRetirementTests
{
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

    static ValueTask<IPantsDatabase> OpenAsync(string root, HttpClient client) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(
                    root,
                    new PantsCloudStorageLocation(
                        new PantsAzureBlobProvider(
                            "account",
                            "container",
                            new Uri("https://storage.example.test"),
                            new PantsAzureCredentialSource.SasToken("sig=test")),
                        "database"))
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));

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
