namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsHybridReadCancellationContractTests
{
    const long LocalBudgetBytes = 128 * 1024;
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);
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
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        using var cancellation = new CancellationTokenSource();
        handler.Arm();

        var read = reader.GetAsync("hybrid-key"u8.ToArray(), cancellation.Token).AsTask();
        try
        {
            await handler.WaitUntilRequestStartsAsync(AssertionTimeout);
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
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        using var cancellation = new CancellationTokenSource();

        var read = reader.GetWithDiagnosticsAsync(
            "hybrid-key"u8.ToArray(),
            cancellation.Token).AsTask();
        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            cancellation.Cancel();
        }
        finally
        {
            failpoint.Release();
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(AssertionTimeout));
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
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        using var cancellation = new CancellationTokenSource();
        var enumerator = scan.GetAsyncEnumerator(cancellation.Token);

        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            cancellation.Cancel();
        }
        finally
        {
            failpoint.Release();
        }

        var exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext.WaitAsync(AssertionTimeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    static async ValueTask<IPantsDatabase> CreateDatabaseWithEvictedSstAsync(
        string path,
        IPantsFailpointHandler failpoints)
    {
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "hybrid-cancellation/")
                .WithSimulatedCloudLocalStorageBudget(LocalBudgetBytes)
                .WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        try
        {
            await using var writer = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put("hybrid-key"u8.ToArray(), CreateValue(256 * 1024, 97));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.FlushAsync(database.DefaultColumnFamily);
            Assert.Empty(Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    static async ValueTask<IPantsDatabase> CreateProviderDatabaseWithMissingLocalSstAsync(
        string path,
        HttpClient client)
    {
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "hybrid-cancellation");
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(path, location).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(cloudHttpClient: client));
        try
        {
            await using var writer = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put("hybrid-key"u8.ToArray(), CreateValue(256 * 1024, 101));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.FlushAsync(database.DefaultColumnFamily);
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
