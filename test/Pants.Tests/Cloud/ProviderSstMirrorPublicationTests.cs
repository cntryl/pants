using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

/// <summary>
///     Provider SST publication through the engine: each flush or compaction publishes only its own
///     new outputs, verifies them with bounded identity-pinned ranges, and never downloads an SST
///     that was already published.
/// </summary>
public sealed class ProviderSstMirrorPublicationTests
{
    [Fact]
    public async Task ShouldPublishOnlyNewFlushOutputGivenPreviouslyPublishedSsts()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var recorder = new SstRequestRecordingHandler(azure);
        using var client = new HttpClient(recorder);
        await using var database = await OpenAsync(cache.Path, new RuntimeDependencies(cloudHttpClient: client));
        for (var flush = 0; flush < 3; flush++)
        {
            await CommitAndFlushAsync(database, $"published-{flush}", 100_000);
        }

        var published = LocalSstNames(cache.Path);
        // Background WAL retirement checks covering SSTs by HEAD; let it finish first.
        await TestWait.UntilAsync(
            () => !azure.ContainsObjectPath("/wal/epochs/"),
            "the covered WAL is retired");
        recorder.Clear();

        await CommitAndFlushAsync(database, "new-output", 100_000);

        var requests = recorder.Requests;
        Assert.Equal(3, published.Length);
        Assert.DoesNotContain(requests, request => published.Contains(request.Name));
        var put = Assert.Single(requests, static request => request.Method == "PUT");
        Assert.DoesNotContain(put.Name, published);
        Assert.DoesNotContain(requests, static request => request.IsWholeGet);
        Assert.Contains(requests, static request => request.IsRangedGet);
        Assert.All(
            requests.Where(static request => request.IsRangedGet),
            request => Assert.True(request.RangeBytes <= ProviderSstPublisher.VerificationRangeBytes));
    }

    [Fact]
    public async Task ShouldNeverDownloadRemoteOnlySstsDuringMirror()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var recorder = new SstRequestRecordingHandler(azure);
        using var client = new HttpClient(recorder);
        await using var database = await OpenAsync(
            cache.Path,
            new RuntimeDependencies(
                cloudHttpClient: client,
                hybridLocalStorageBudgetBytes: 512 * 1024));
        await CommitAndFlushAsync(database, "remote-first", 128 * 1024);
        await CommitAndFlushAsync(database, "remote-second", 512 * 1024);
        var remoteOnly = azure.GetObjectPaths("/sst/")
            .Select(static path => path[(path.LastIndexOf('/') + 1)..])
            .ToArray();
        recorder.Clear();

        await CommitAndFlushAsync(database, "remote-third", 1024);

        Assert.Equal(2, remoteOnly.Length);
        Assert.DoesNotContain(LocalSstNames(cache.Path), remoteOnly.Contains);
        Assert.DoesNotContain(
            recorder.Requests,
            request => remoteOnly.Contains(request.Name) && request.Method is "GET" or "PUT");
        Assert.Equal(3, azure.GetObjectPaths("/sst/").Length);
    }

    [Fact]
    public async Task ShouldRevalidatePublishedSstsByIdentityWithoutDownloadingThemAfterReopen()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var recorder = new SstRequestRecordingHandler(azure);
        using var client = new HttpClient(recorder);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var first = await OpenAsync(cache.Path, dependencies))
        {
            for (var flush = 0; flush < 3; flush++)
            {
                await CommitAndFlushAsync(first, $"before-reopen-{flush}", 100_000);
            }
        }

        var published = LocalSstNames(cache.Path);
        await using var reopened = await OpenAsync(cache.Path, dependencies);
        recorder.Clear();

        await CommitAndFlushAsync(reopened, "after-reopen", 100_000);

        var previous = recorder.Requests.Where(request => published.Contains(request.Name)).ToArray();
        Assert.Equal(3, published.Length);
        Assert.All(previous, static request => Assert.Equal("HEAD", request.Method));
        Assert.True(previous.Length <= published.Length);
        Assert.Single(recorder.Requests, static request => request.Method == "PUT");
        await AssertValueAsync(reopened, "before-reopen-1", 100_000);
    }

    [Fact]
    public async Task ShouldVerifyCompactionOutputsWithBoundedRangedReadback()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var recorder = new SstRequestRecordingHandler(azure);
        using var client = new HttpClient(recorder);
        await using var database = await OpenAsync(cache.Path, new RuntimeDependencies(cloudHttpClient: client));
        for (var flush = 0; flush < 4; flush++)
        {
            await CommitAndFlushAsync(database, $"compaction-{flush}", 100_000);
        }

        recorder.Clear();

        await database.Maintenance.CompactAllAsync();

        var requests = recorder.Requests;
        Assert.Contains(requests, static request => request.Method == "PUT");
        Assert.DoesNotContain(requests, static request => request.IsWholeGet);
        Assert.All(
            requests.Where(static request => request.IsRangedGet),
            request => Assert.True(request.RangeBytes <= ProviderSstPublisher.VerificationRangeBytes));
        await AssertValueAsync(database, "compaction-2", 100_000);
    }

    [Fact]
    public async Task ShouldSizeCloudCompactionOutputsSoTheirPublicationFitsTheMaintenancePool()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(azure);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(cache.Path).WithMemoryBudget(PantsMemoryBudget.FromBytes(64L * 1024 * 1024)),
            new RuntimeDependencies(cloudHttpClient: client));
        for (var flush = 0; flush < 4; flush++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var key = 0; key < 64; key++)
            {
                transaction.Put(
                    TestBytes.FromString($"pool-{flush}-{key:000}"),
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16 * 1024));
            }

            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await database.Maintenance.CompactAllAsync();

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        // Hybrid compaction drains each output to the provider and releases its local copy, so
        // the published layout is the record of what was written.
        var layout = (await database.Diagnostics.GetStorageLayoutAsync())
            .Levels.SelectMany(static level => level.Files)
            .ToArray();
        var outputs = layout.Select(static file => file.SizeBytes).ToArray();
        Assert.All(
            layout.Where(static file => file.Level > 0),
            file => Assert.False(File.Exists(Path.Combine(cache.Path, "sst", file.Name))));
        Assert.True(outputs.Length > 1, "Outputs should be partitioned to fit the publication envelope.");
        Assert.All(
            outputs,
            size => Assert.True(
                ImmutablePublicationEnvelope.For(size) <= metrics.CompactionBufferCapacityBytes,
                $"A {size}-byte output cannot be admitted by a {metrics.CompactionBufferCapacityBytes}-byte pool."));
        Assert.Equal(0, metrics.CompactionBufferUsedBytes);
        Assert.Equal(outputs.Length, azure.GetObjectPaths("/sst/").Length);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string root, RuntimeDependencies dependencies) =>
        PantsDatabase.OpenForTestingAsync(CreateOptions(root), dependencies);

    static PantsOpenOptions CreateOptions(string root) =>
        PantsOpenOptions.Cloud(
                root,
                new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider(
                        "account",
                        "container",
                        new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")),
                    "database"))
            .WithBackgroundCompaction(false);

    static async ValueTask CommitAndFlushAsync(IPantsDatabase database, string key, int valueBytes)
    {
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString(key), CreateValue(valueBytes));
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async ValueTask AssertValueAsync(IPantsDatabase database, string key, int valueBytes)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = Assert.IsType<ReadOnlyMemory<byte>>(
            await transaction.GetAsync(TestBytes.FromString(key)));
        Assert.Equal(CreateValue(valueBytes), value.ToArray());
    }

    static byte[] CreateValue(int bytes)
    {
        var value = new byte[bytes];
        new Random(bytes).NextBytes(value);
        return value;
    }

    static string[] LocalSstNames(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "sst"), "*.sst", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
