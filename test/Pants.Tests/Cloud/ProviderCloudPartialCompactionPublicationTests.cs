using System.Net;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudPartialCompactionPublicationTests
{
    const int FlushCount = 4;
    const int EntriesPerFlush = 64;
    const int ValueBytes = 16 * 1024;

    [Fact]
    public async Task ShouldReclaimFirstRemotePartitionAndRecoverInputsWhenSecondUploadFails()
    {
        using var cache = new TemporaryDirectory();
        using var azure = new InMemoryAzureBlobHandler();
        using var failingHandler = new FailSecondSstPutHandler(azure);
        using var client = new HttpClient(failingHandler, false);
        var options = CreateOptions(cache.Path);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        string[] inputNames;
        string[] remoteInputNames;
        string orphanName;

        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await SeedAsync(database);
            inputNames = await LayoutNamesAsync(database);
            remoteInputNames = RemoteSstNames(azure);
            failingHandler.Arm();

            await Assert.ThrowsAsync<PantsIOException>(() =>
                database.Maintenance.CompactAllAsync().AsTask());

            Assert.Equal(2, failingHandler.SstPutAttempts);
            Assert.Equal(inputNames, await LayoutNamesAsync(database));
            using (var failedIntent = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(cache.Path, "intent_log.json"))))
            {
                var publication = Assert.Single(failedIntent.RootElement.EnumerateArray())
                    .GetProperty("CompactionPublish");
                Assert.Equal("OutputDurable", publication.GetProperty("phase").GetString());
                Assert.Equal(2, publication.GetProperty("added").GetArrayLength());
            }

            var remoteAfterFailure = RemoteSstNames(azure);
            orphanName = Assert.Single(remoteAfterFailure.Except(remoteInputNames, StringComparer.Ordinal));
            Assert.Equal(remoteInputNames.Length + 1, remoteAfterFailure.Length);
        }

        using (var localIntentAfterClose = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(cache.Path, "intent_log.json"))))
        {
            Assert.NotEmpty(localIntentAfterClose.RootElement.EnumerateArray());
        }

        RemoveLocalSsts(cache.Path);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var recovery = await reopened.Diagnostics.GetRecoveryMetricsAsync();
        Assert.Equal(1, recovery.IntentLogReplayRuns);
        Assert.Equal(1, recovery.IntentLogEntriesReplayed);

        using (var localIntent = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(cache.Path, "intent_log.json"))))
        {
            Assert.Empty(localIntent.RootElement.EnumerateArray());
        }

        using (var remoteIntent = JsonDocument.Parse(azure.GetObjectText("intent_log.json")))
        {
            Assert.Empty(remoteIntent.RootElement.EnumerateArray());
        }

        Assert.Equal(inputNames, await LayoutNamesAsync(reopened));
        Assert.DoesNotContain(orphanName, RemoteSstNames(azure), StringComparer.Ordinal);
        Assert.Equal(remoteInputNames, RemoteSstNames(azure));
        await AssertEverySeededKeyAsync(reopened);
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
                    "partial-compaction"))
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64L * 1024 * 1024));

    static async Task SeedAsync(IPantsDatabase database)
    {
        for (var flush = 0; flush < FlushCount; flush++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var entry = 0; entry < EntriesPerFlush; entry++)
                {
                    transaction.Put(Key((flush * EntriesPerFlush) + entry), CreateValue());
                }

                await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
    }

    static async Task AssertEverySeededKeyAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < FlushCount * EntriesPerFlush; index++)
        {
            var value = Assert.IsType<ReadOnlyMemory<byte>>(
                await transaction.GetAsync(Key(index)));
            Assert.Equal(CreateValue(), value.ToArray());
        }
    }

    static async Task<string[]> LayoutNamesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync())
        .Levels.SelectMany(static level => level.Files)
        .Select(static file => file.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    static string[] RemoteSstNames(InMemoryAzureBlobHandler handler) =>
        handler.GetObjectPaths("/sst/")
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    static void RemoveLocalSsts(string path)
    {
        foreach (var sst in Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"))
        {
            File.Delete(sst);
        }
    }

    static byte[] Key(int index) => TestBytes.FromString($"partial:{index:D4}");

    static byte[] CreateValue()
    {
        var value = new byte[ValueBytes];
        new Random(ValueBytes).NextBytes(value);
        return value;
    }

    sealed class FailSecondSstPutHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        int _armed;
        int _sstPutAttempts;

        public int SstPutAttempts => Volatile.Read(ref _sstPutAttempts);

        public void Arm()
        {
            Volatile.Write(ref _sstPutAttempts, 0);
            Volatile.Write(ref _armed, 1);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isSstPut = request.Method == HttpMethod.Put &&
                           request.RequestUri?.AbsolutePath.Contains("/sst/", StringComparison.Ordinal) == true;
            if (isSstPut && Volatile.Read(ref _armed) != 0 &&
                Interlocked.Increment(ref _sstPutAttempts) == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("<Error><Code>InternalError</Code></Error>")
                });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
