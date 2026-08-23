using System.Text;

namespace Cntryl.Pants.Tests.Cloud;

[Trait("Category", "Sqrzl")]
public sealed class SqrzlCloudProviderQualificationTests
{
    const string Endpoint = "http://127.0.0.1:9000";
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

    static async Task RunObjectContractAsync(PantsCloudStorageLocation providerLocation)
    {
        await RequireSqrzlAsync();
        var location = providerLocation with
        {
            Prefix = $"qualification/{Guid.NewGuid():N}"
        };
        var store = CloudObjectStoreFactory.Create(location, TimeSpan.FromSeconds(10));
        const string key = "objects/value.bin";
        const string emptyKey = "objects/empty.bin";
        var initial = "hello-sqrzl"u8.ToArray();
        var updated = "updated"u8.ToArray();

        Assert.True(await store.PutAsync(
            key,
            initial,
            new CloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        Assert.False(await store.PutAsync(
            key,
            "duplicate"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
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
            new CloudObjectWriteCondition.IfVersion(read.Version),
            CancellationToken.None));
        Assert.False(await store.PutAsync(
            key,
            initial,
            new CloudObjectWriteCondition.IfVersion(read.Version),
            CancellationToken.None));
        Assert.Equal(
            updated,
            (await store.GetAsync(key, CancellationToken.None))!.Data.ToArray());

        Assert.True(await store.PutAsync(
            emptyKey,
            ReadOnlyMemory<byte>.Empty,
            new CloudObjectWriteCondition.Unconditional(),
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
                new CloudObjectDeleteCondition.IfVersion(read.Version),
                CancellationToken.None));
        var current = Assert.IsType<CloudObject>(
            await store.GetAsync(key, CancellationToken.None));
        Assert.Equal(
            CloudObjectDeleteOutcome.Deleted,
            await store.DeleteAsync(
                key,
                new CloudObjectDeleteCondition.IfVersion(current.Version),
                CancellationToken.None));
        var missingDelete = await store.DeleteAsync(
            key,
            new CloudObjectDeleteCondition.Unconditional(),
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
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("engine-key"u8.ToArray(), "engine-value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.FlushAsync(database.DefaultColumnFamily);
            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using var recovered = await PantsDatabase.OpenAsync(
            PantsOpenOptions.CloudMulti(replacementCache.Path, topology)
                .WithBackgroundCompaction(false));
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("engine-key"u8.ToArray()));
        Assert.Equal("engine-value", Encoding.UTF8.GetString(value.Span));
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
            throw new Xunit.Sdk.XunitException(
                $"Sqrzl qualification requires a running emulator at {Endpoint}. " +
                "Start it with 'docker compose up -d sqrzl'.",
                exception);
        }
    }

    static PantsCloudStorageLocation CreateS3Location() => new(
        new PantsCloudProviderConfiguration.S3Compatible(
            "pants-sqrzl-s3",
            "us-east-1",
            new Uri(Endpoint),
            true,
            new PantsS3CredentialSource.StaticCredentials("admin", "easy-peasy")),
        string.Empty);

    static PantsCloudStorageLocation CreateAzureLocation() => new(
        new PantsCloudProviderConfiguration.AzureBlob(
            "admin",
            "pants-sqrzl-azure",
            new Uri($"{Endpoint}/admin"),
            new PantsAzureCredentialSource.SharedKey(
                Convert.ToBase64String("easy-peasy"u8))),
        string.Empty);

    static PantsCloudStorageLocation CreateGcsXmlLocation() => new(
        new PantsCloudProviderConfiguration.Gcs(
            "pants-sqrzl-gcs-xml",
            "sqrzl",
            new Uri(Endpoint),
            PantsGcsApiStyle.Xml,
            new PantsGcsCredentialSource.HmacKey("admin", "easy-peasy")),
        string.Empty);

    static PantsCloudStorageLocation CreateGcsJsonLocation() => new(
        new PantsCloudProviderConfiguration.Gcs(
            "pants-sqrzl-gcs-json",
            "sqrzl",
            new Uri(Endpoint),
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.BearerToken("admin")),
        string.Empty);
}
