using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudHybridCacheTests
{
    const long LocalCacheBudgetBytes = 280 * 1024;

    [Fact]
    public async Task ShouldEvictProvenSstsAndAcceptWritesWhileProviderGarbageCollectionIsUnavailable()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path),
            new RuntimeDependencies(
                cloudHttpClient: client,
                hybridLocalStorageBudgetBytes: LocalCacheBudgetBytes));
        handler.FailSstList = true;

        for (var index = 0; index < 6; index++)
        {
            await PutAndFlushAsync(database, $"residue-{index}", index);
        }

        Assert.Empty(LocalSsts(cache.Path));
        Assert.True(handler.GetObjectPaths("/sst/").Count(static path => path.EndsWith(
            ".sst",
            StringComparison.Ordinal)) >= 6);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.HybridUsagePercent < 90);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);

        await PutAndFlushAsync(database, "write-after-residue", 7);
        Assert.Empty(LocalSsts(cache.Path));
        await AssertReadableAsync(database, "residue-5");
        await AssertReadableAsync(database, "write-after-residue");
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
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString(key), value);
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task AssertReadableAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(256 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(
            await transaction.GetAsync(TestBytes.FromString(key))).Length);
    }
}
