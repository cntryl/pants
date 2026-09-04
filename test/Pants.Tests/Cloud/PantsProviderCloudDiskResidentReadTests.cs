using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsProviderCloudDiskResidentReadTests
{
    [Fact]
    public async Task ShouldUseOnlyRangedProviderReadsForColdOpenAndPointLookup()
    {
        using var directory = new TemporaryDirectory();
        var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 96; index++)
            {
                writer.Put(Key(index), Value(index));
            }

            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        foreach (var path in Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"))
        {
            File.Delete(path);
        }

        handler.ResetSstReadMetrics();
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var value = await reader.GetAsync(Key(48));

        Assert.Equal(Value(48), value?.ToArray());
        Assert.Equal(0, handler.SstFullReads);
        Assert.True(handler.SstRangeReads > 0);
        Assert.InRange(handler.SstRangeBytes, 1, handler.SstStoredBytes - 1);
        handler.ResetSstReadMetrics();

        await using var scan = await reader.ScanAsync(new PantsScanQuery
        {
            Prefix = "provider:007"u8.ToArray()
        });
        var actual = new List<string>();
        await foreach (var entry in scan)
        {
            actual.Add(TestBytes.ToText(entry.Key));
        }

        Assert.Equal(
            Enumerable.Range(70, 10).Select(static index => $"provider:{index:D4}"),
            actual);
        Assert.Equal(0, handler.SstFullReads);
        Assert.InRange(handler.SstRangeBytes, 1, handler.SstStoredBytes - 1);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
    }

    [Fact]
    public async Task ShouldRejectAReplacedRemoteSstWithoutCacheAdmission()
    {
        using var directory = new TemporaryDirectory();
        var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = CreateOptions(directory.Path);
        await CreateCorpusAsync(options, client);
        RemoveLocalSsts(directory.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        handler.ReplaceSstOnNextRange();

        await Assert.ThrowsAnyAsync<PantsException>(() => reader.GetAsync(Key(48)).AsTask());

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Equal(0, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).BlockCacheUsedBytes);
    }

    [Fact]
    public async Task ShouldTimeOutARemoteRangeReadWithoutCacheAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new GatedSstReadHttpHandler(new InMemoryAzureBlobHandler());
        using var client = new HttpClient(handler, false);
        var options = CreateOptions(directory.Path)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(50));
        await CreateCorpusAsync(options, client);
        RemoveLocalSsts(directory.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        handler.Arm();
        try
        {
            await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                reader.GetAsync(Key(48)).AsTask());
        }
        finally
        {
            handler.Release();
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Equal(0, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).BlockCacheUsedBytes);
    }

    [Fact]
    public async Task ShouldRemoveIncompleteHydrationStagingGivenCompactionIsCancelled()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new GatedSstReadHttpHandler(new InMemoryAzureBlobHandler());
        using var client = new HttpClient(handler, false);
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(cloudHttpClient: client)))
        {
            for (var batch = 0; batch < 3; batch++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(Key(batch), Value(batch));
                await writer.CommitAsync(PantsWriteOptions.CloudStrict);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }

        RemoveLocalSsts(directory.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        using var cancellation = new CancellationTokenSource();
        handler.Arm();
        var compaction = reopened.Maintenance.CompactAllAsync(cancellation.Token).AsTask();
        try
        {
            await handler.WaitUntilRequestStartsAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compaction);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            handler.Release();
        }

        // The canceled caller can observe its response before the admitted actor command finishes
        // unwinding. This query is ordered behind that command and establishes the cleanup boundary.
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(0, metrics.CompactionBufferUsedBytes);
        Assert.True(metrics.CompactionBufferPeakBytes <= metrics.CompactionBufferCapacityBytes);
    }

    static async Task CreateCorpusAsync(PantsOpenOptions options, HttpClient client)
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 96; index++)
        {
            writer.Put(Key(index), Value(index));
        }

        await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static void RemoveLocalSsts(string path)
    {
        foreach (var local in Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"))
        {
            File.Delete(local);
        }
    }

    static PantsOpenOptions CreateOptions(string path)
    {
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "disk-resident");
        return PantsOpenOptions.Cloud(path, location).WithBackgroundCompaction(false);
    }

    static byte[] Key(int index) => TestBytes.FromString($"provider:{index:D4}");

    static byte[] Value(int index)
    {
        var value = new byte[2 * 1024];
        new Random(index).NextBytes(value);
        return value;
    }
}
