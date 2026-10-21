using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsHybridReadCancellationContractTests
{
    const long LocalBudgetBytes = 280 * 1024;
    static readonly TimeSpan PromptCancellationTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ShouldCancelProviderSstFetchPromptlyGivenCallerCancelsAfterRequestStarts()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new GatedSstReadHttpHandler(new InMemoryAzureBlobHandler());
        using var client = new HttpClient(handler, false);
        await using var database = await CreateProviderDatabaseWithMissingLocalSstAsync(
            directory.Path,
            client);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        using var cancellation = new CancellationTokenSource();
        handler.Arm();

        var read = reader.GetAsync("hybrid-key"u8.ToArray(), cancellation.Token).AsTask();
        try
        {
            await handler.WaitUntilRequestStartsAsync(TestTimeouts.Expected);
            cancellation.Cancel();

            var exception =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    read.WaitAsync(PromptCancellationTimeout));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            handler.Release();
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
    }

    [Fact]
    public async Task ShouldPreserveCallerCancellationGivenHybridPointReadWasAdmitted()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new HybridHydrationFailpointHandler();
        await using var database = await CreateDatabaseWithEvictedSstAsync(
            directory.Path,
            failpoint);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        using var cancellation = new CancellationTokenSource();

        failpoint.Arm();
        var read = reader.GetWithDiagnosticsAsync(
            "hybrid-key"u8.ToArray(),
            cancellation.Token).AsTask();
        try
        {
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            cancellation.Cancel();
        }
        finally
        {
            failpoint.Release();
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ShouldPreserveCallerCancellationGivenHybridScanWasAdmitted()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new HybridHydrationFailpointHandler();
        await using var database = await CreateDatabaseWithEvictedSstAsync(
            directory.Path,
            failpoint);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        using var cancellation = new CancellationTokenSource();
        var enumerator = scan.GetAsyncEnumerator(cancellation.Token);

        failpoint.Arm();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            cancellation.Cancel();
        }
        finally
        {
            failpoint.Release();
        }

        var exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await enumerator.DisposeAsync();
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.ScanBufferPeakBytes <= metrics.ScanBufferCapacityBytes);
        Assert.Equal(0, metrics.ScanBufferUsedBytes);
    }

    static async ValueTask<IPantsDatabase> CreateDatabaseWithEvictedSstAsync(
        string path,
        IFailpointHandler failpoints)
    {
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "hybrid-cancellation/")
                .WithSimulatedCloudLocalStorageBudget(LocalBudgetBytes)
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        try
        {
            var value = CreateValue(24 * 1024, 97);
            await CommitAndFlushAsync(database, "hybrid-key", value);
            var firstSstName = Path.GetFileName(Assert.Single(Directory.GetFiles(
                Path.Combine(path, "cloud_store", "sst"),
                "*.sst")));
            for (var index = 0; index < 12; index++)
            {
                await CommitAndFlushAsync(
                    database,
                    $"eviction-seed-{index}",
                    CreateValue(value.Length, 98 + index));
            }

            await CommitAndFlushAsync(database, "eviction-window", CreateValue(value.Length, 110));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            Assert.DoesNotContain(
                Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"),
                file => Path.GetFileName(file) == firstSstName);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    static async Task CommitAndFlushAsync(
        IPantsDatabase database,
        string key,
        byte[] value)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        writer.Put(TestBytes.FromString(key), value);
        await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async ValueTask<IPantsDatabase> CreateProviderDatabaseWithMissingLocalSstAsync(
        string path,
        HttpClient client)
    {
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "hybrid-cancellation");
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(path, location).WithBackgroundCompaction(false),
            new RuntimeDependencies(cloudHttpClient: client));
        try
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put("hybrid-key"u8.ToArray(), CreateValue(256 * 1024, 101));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            File.Delete(Assert.Single(Directory.GetFiles(Path.Combine(path, "sst"), "*.sst")));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    static byte[] CreateValue(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }
}
