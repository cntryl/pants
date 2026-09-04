using System.Text;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Cloud;

[Trait("Category", "Sqrzl")]
public sealed class SqrzlCloudProviderQualificationTests
{
    static readonly string Endpoint = ResolveEndpoint();

    static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    [Fact]
    public Task ShouldSatisfyObjectContractGivenSqrzlS3() =>
        RunObjectContractAsync(CreateS3Location());

    [Fact]
    public Task ShouldSatisfyObjectContractGivenSqrzlAzureBlob() =>
        RunObjectContractAsync(CreateAzureLocation());

    [Fact]
    public Task ShouldSatisfyObjectContractGivenSqrzlGcsXml() =>
        RunObjectContractAsync(CreateGcsXmlLocation());

    [Fact]
    public Task ShouldSatisfyObjectContractGivenSqrzlGcsJson() =>
        RunObjectContractAsync(CreateGcsJsonLocation());

    [Fact]
    public Task ShouldRecoverAfterCacheLossGivenSqrzlS3() =>
        RecoverAfterCacheLossAsync(CreateS3Location());

    [Fact]
    public Task ShouldRecoverAfterCacheLossGivenSqrzlAzureBlob() =>
        RecoverAfterCacheLossAsync(CreateAzureLocation());

    [Fact]
    public Task ShouldRecoverAfterCacheLossGivenSqrzlGcsXml() =>
        RecoverAfterCacheLossAsync(CreateGcsXmlLocation());

    [Fact]
    public Task ShouldRecoverAfterCacheLossGivenSqrzlGcsJson() =>
        RecoverAfterCacheLossAsync(CreateGcsJsonLocation());

    [Fact]
    public Task ShouldRecoverGivenSqrzlTwoProviderTopology() =>
        RecoverAfterCacheLossAsync(new PantsCloudStorageTopology(
            CreateS3Location(),
            CreateS3Location(),
            CreateGcsJsonLocation()));

    [Fact]
    public Task ShouldRecoverGivenSqrzlThreeProviderTopology() =>
        RecoverAfterCacheLossAsync(new PantsCloudStorageTopology(
            CreateS3Location(),
            CreateGcsXmlLocation(),
            CreateAzureLocation()));

    [Fact]
    public Task ShouldPreserveAcceptedDurabilityAfterDeadlineGivenSqrzlS3() =>
        PreserveAcceptedDurabilityAfterDeadlineAsync(CreateS3Location());

    [Fact]
    public Task ShouldPreserveAcceptedDurabilityAfterDeadlineGivenSqrzlAzureBlob() =>
        PreserveAcceptedDurabilityAfterDeadlineAsync(CreateAzureLocation());

    [Fact]
    public Task ShouldPreserveAcceptedDurabilityAfterDeadlineGivenSqrzlGcs() =>
        PreserveAcceptedDurabilityAfterDeadlineAsync(CreateGcsJsonLocation());

    static async Task RunObjectContractAsync(PantsCloudStorageLocation providerLocation)
    {
        await RequireSqrzlAsync();
        var location = providerLocation with
        {
            Prefix = $"qualification/{Guid.NewGuid():N}"
        };
        await using var store = await CloudObjectStoreFactory.CreateAsync(
            location,
            TimeSpan.FromSeconds(10));
        const string key = "objects/value.bin";
        const string emptyKey = "objects/empty.bin";
        var initial = "hello-sqrzl"u8.ToArray();
        var updated = "updated"u8.ToArray();

        Assert.True(await store.PutAsync(
            key,
            initial,
            new PantsCloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        Assert.False(await store.PutAsync(
            key,
            "duplicate"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));

        var read = Assert.IsType<CloudObject>(
            await store.GetAsync(key, CancellationToken.None));
        Assert.Equal(initial, read.Data.ToArray());

        var metadata = Assert.IsType<CloudObjectMetadata>(
            await store.HeadAsync(key, CancellationToken.None));
        Assert.Equal((ulong)initial.Length, metadata.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Version));

        Assert.True(await store.PutAsync(
            key,
            updated,
            new PantsCloudObjectWriteCondition.IfVersion(read.Version),
            CancellationToken.None));
        Assert.False(await store.PutAsync(
            key,
            initial,
            new PantsCloudObjectWriteCondition.IfVersion(read.Version),
            CancellationToken.None));
        Assert.Equal(
            updated,
            (await store.GetAsync(key, CancellationToken.None))!.Data.ToArray());

        Assert.True(await store.PutAsync(
            emptyKey,
            ReadOnlyMemory<byte>.Empty,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));
        Assert.Empty((await store.GetAsync(emptyKey, CancellationToken.None))!.Data.ToArray());

        var listed = await store.ListAllAsync("objects/", CancellationToken.None);
        Assert.Contains(key, listed);
        Assert.Contains(emptyKey, listed);
        Assert.Null(await store.GetAsync("objects/missing.bin", CancellationToken.None));

        Assert.Equal(
            CloudObjectDeleteOutcome.ConditionNotMet,
            await store.DeleteAsync(
                key,
                new PantsCloudObjectDeleteCondition.IfVersion(read.Version),
                CancellationToken.None));
        var current = Assert.IsType<CloudObject>(
            await store.GetAsync(key, CancellationToken.None));
        Assert.Equal(
            CloudObjectDeleteOutcome.Deleted,
            await store.DeleteAsync(
                key,
                new PantsCloudObjectDeleteCondition.IfVersion(current.Version),
                CancellationToken.None));
        var missingDelete = await store.DeleteAsync(
            key,
            new PantsCloudObjectDeleteCondition.Unconditional(),
            CancellationToken.None);
        Assert.Contains(
            missingDelete,
            new[] { CloudObjectDeleteOutcome.Deleted, CloudObjectDeleteOutcome.NotFound });
    }

    static Task RecoverAfterCacheLossAsync(PantsCloudStorageLocation location) =>
        RecoverAfterCacheLossAsync(PantsCloudStorageTopology.Shared(location));

    static async Task RecoverAfterCacheLossAsync(PantsCloudStorageTopology topology)
    {
        await RequireSqrzlAsync();
        using var sourceCache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        var prefix = $"engine/{Guid.NewGuid():N}";
        topology = new PantsCloudStorageTopology(
            topology.Wal with { Prefix = prefix },
            topology.Sst with { Prefix = prefix },
            topology.Control with { Prefix = prefix });

        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.CloudMulti(sourceCache.Path, topology)
                             .WithBackgroundCompaction(false)))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("engine-key"u8.ToArray(), "engine-value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using var recovered = await PantsDatabase.OpenAsync(
            PantsOpenOptions.CloudMulti(replacementCache.Path, topology)
                .WithBackgroundCompaction(false));
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("engine-key"u8.ToArray()));
        Assert.Equal("engine-value", Encoding.UTF8.GetString(value.Span));
    }

    static async Task PreserveAcceptedDurabilityAfterDeadlineAsync(
        PantsCloudStorageLocation providerLocation)
    {
        await RequireSqrzlAsync();
        using var sourceCache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        using var failpoint = new BlockingCloudWalUploadFailpointHandler();
        var location = providerLocation with
        {
            Prefix = $"deadline/{Guid.NewGuid():N}"
        };
        var options = PantsOpenOptions.CloudMulti(
                sourceCache.Path,
                PantsCloudStorageTopology.Shared(location))
            .WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(250))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(500));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("accepted-key"u8.ToArray(), "accepted-value"u8.ToArray());
            var commit = transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
            await failpoint.WaitUntilEnteredAsync(TimeSpan.FromSeconds(5));
            try
            {
                var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                    commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                failpoint.Release();
            }
        }

        await WaitForLateResponseAsync(database);
        await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        await database.DisposeAsync();

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.CloudMulti(
                    replacementCache.Path,
                    PantsCloudStorageTopology.Shared(location))
                .WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "accepted-value"u8.ToArray(),
            (await reader.GetAsync("accepted-key"u8.ToArray()))?.ToArray());
    }

    static async Task WaitForLateResponseAsync(IPantsDatabase database)
    {
        var timeout = TimeSpan.FromSeconds(10);
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync(cancellation.Token);
            if (metrics.RuntimeLateResponsesTotal >= 1)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("The accepted Sqrzl cloud obligation did not finish.");
    }

    static async Task RequireSqrzlAsync()
    {
        try
        {
            using var response = await HealthClient.GetAsync(
                $"{Endpoint}/healthz",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.True(
                response.IsSuccessStatusCode,
                $"Sqrzl qualification requires a healthy emulator at {Endpoint}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new XunitException(
                $"Sqrzl qualification requires a running emulator at {Endpoint}. " +
                "Start it with 'docker compose up -d sqrzl'.",
                exception);
        }
    }

    static string ResolveEndpoint()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("PANTS_SQRZL_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return configuredEndpoint.TrimEnd('/');
        }

        var configuredPort = Environment.GetEnvironmentVariable("PANTS_SQRZL_API_PORT");
        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            configuredPort = "9000";
        }

        if (!ushort.TryParse(configuredPort, out var port) || port == 0)
        {
            throw new InvalidOperationException(
                "PANTS_SQRZL_API_PORT must be an integer from 1 through 65535.");
        }

        return $"http://127.0.0.1:{port}";
    }

    static PantsCloudStorageLocation CreateS3Location() => new(
        new PantsS3CompatibleProvider(
            "pants-sqrzl-s3",
            "us-east-1",
            new Uri(Endpoint),
            true,
            new PantsS3CredentialSource.StaticCredentials("admin", "easy-peasy")),
        string.Empty);

    static PantsCloudStorageLocation CreateAzureLocation() => new(
        new PantsAzureBlobProvider(
            "admin",
            "pants-sqrzl-azure",
            new Uri($"{Endpoint}/admin"),
            new PantsAzureCredentialSource.SharedKey(
                Convert.ToBase64String("easy-peasy"u8))),
        string.Empty);

    static PantsCloudStorageLocation CreateGcsXmlLocation() => new(
        new PantsGcsProvider(
            "pants-sqrzl-gcs-xml",
            "sqrzl",
            new Uri(Endpoint),
            PantsGcsApiStyle.Xml,
            new PantsGcsCredentialSource.HmacKey("admin", "easy-peasy")),
        string.Empty);

    static PantsCloudStorageLocation CreateGcsJsonLocation() => new(
        new PantsGcsProvider(
            "pants-sqrzl-gcs-json",
            "sqrzl",
            new Uri(Endpoint),
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.BearerToken("admin")),
        string.Empty);
}
