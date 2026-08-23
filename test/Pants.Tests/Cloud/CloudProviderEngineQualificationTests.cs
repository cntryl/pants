namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudProviderEngineQualificationTests
{
    [Fact]
    public async Task ShouldRecoverEngineFromSqrzlS3AfterLocalCacheLoss()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var location = CreateS3Location("s3.example.test", "database");

        await WriteFlushRecoverAsync(
            PantsCloudStorageTopology.Shared(location),
            client);

        Assert.True(handler.ContainsObject("s3.example.test", "/wal/epochs/"));
        Assert.True(handler.ContainsObject("s3.example.test", "/sst/"));
        Assert.True(handler.ContainsObject("s3.example.test", "/metadata/manifest"));
    }

    [Fact]
    public async Task ShouldRecoverEngineFromAwsS3ProtocolAfterLocalCacheLoss()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AwsS3(
                "bucket",
                "us-east-1",
                new PantsS3CredentialSource.StaticCredentials("access", "secret")),
            "database");

        await WriteFlushRecoverAsync(
            PantsCloudStorageTopology.Shared(location),
            client);

        Assert.True(handler.ContainsObject(
            "bucket.s3.us-east-1.amazonaws.com",
            "/wal/epochs/"));
        Assert.True(handler.ContainsObject(
            "bucket.s3.us-east-1.amazonaws.com",
            "/sst/"));
    }

    [Fact]
    public async Task ShouldRecoverEngineFromSqrzlGcsJsonAfterLocalCacheLoss()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var location = CreateGcsLocation("gcs.example.test", "database");

        await WriteFlushRecoverAsync(
            PantsCloudStorageTopology.Shared(location),
            client);

        Assert.True(handler.ContainsObject("gcs.example.test", "/wal/epochs/"));
        Assert.True(handler.ContainsObject("gcs.example.test", "/sst/"));
        Assert.True(handler.ContainsObject("gcs.example.test", "/metadata/manifest"));
    }

    [Fact]
    public async Task ShouldRecoverEngineFromGcsXmlAfterLocalCacheLoss()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.Gcs(
                "bucket",
                "project",
                new Uri("https://gcs-xml.example.test"),
                PantsGcsApiStyle.Xml,
                new PantsGcsCredentialSource.HmacKey("access", "secret")),
            "database");

        await WriteFlushRecoverAsync(
            PantsCloudStorageTopology.Shared(location),
            client);

        Assert.True(handler.ContainsObject("gcs-xml.example.test", "/wal/epochs/"));
        Assert.True(handler.ContainsObject("gcs-xml.example.test", "/sst/"));
    }

    [Fact]
    public async Task ShouldRouteThreeLocationTopologyThroughSqrzl()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var topology = new PantsCloudStorageTopology(
            CreateS3Location("wal.example.test", "database"),
            CreateGcsLocation("sst.example.test", "database"),
            CreateAzureLocation("control.example.test", "database"));

        await WriteFlushRecoverAsync(topology, client);

        Assert.True(handler.ContainsObject("wal.example.test", "/wal/epochs/"));
        Assert.True(handler.ContainsObject("sst.example.test", "/sst/"));
        Assert.True(handler.ContainsObject("control.example.test", "/metadata/manifest"));
        Assert.False(handler.ContainsObject("control.example.test", "/wal/epochs/"));
    }

    [Fact]
    public async Task ShouldRouteTwoLocationTopologyThroughSqrzl()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        var data = CreateS3Location("data.example.test", "database");
        var control = CreateGcsLocation("control.example.test", "database");
        var topology = PantsCloudStorageTopology.Shared(control).WithWal(data);

        await WriteFlushRecoverAsync(topology, client);

        Assert.True(handler.ContainsObject("data.example.test", "/wal/epochs/"));
        Assert.True(handler.ContainsObject("control.example.test", "/sst/"));
        Assert.True(handler.ContainsObject("control.example.test", "/metadata/manifest"));
        Assert.False(handler.ContainsObject("data.example.test", "/sst/"));
    }

    [Fact]
    public async Task ShouldRecoverEngineFromRealS3AfterLocalCacheLossIfConfigured()
    {
        var bucket = Environment.GetEnvironmentVariable("PANTS_LIVE_S3_BUCKET");
        var region = Environment.GetEnvironmentVariable("PANTS_LIVE_S3_REGION");
        var required = string.Equals(
            Environment.GetEnvironmentVariable("PANTS_REQUIRE_LIVE_S3"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region))
        {
            Assert.False(required, "Live S3 qualification was required but its bucket or region was not configured.");
            return;
        }

        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AwsS3(
                bucket,
                region,
                new PantsS3CredentialSource.Environment()),
            $"pants-qualification/{Guid.NewGuid():N}");
        try
        {
            await WriteFlushRecoverAsync(
                PantsCloudStorageTopology.Shared(location),
                null);
        }
        finally
        {
            await DeleteAllAsync(location);
        }
    }

    static async Task WriteFlushRecoverAsync(
        PantsCloudStorageTopology topology,
        HttpClient? cloudHttpClient)
    {
        using var sourceCache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        var dependencies = new PantsRuntimeDependencies(cloudHttpClient: cloudHttpClient);
        var options = PantsOpenOptions.CloudMulti(sourceCache.Path, topology)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.CloudMulti(replacementCache.Path, topology)
                .WithBackgroundCompaction(false),
            dependencies);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("key"u8.ToArray()))));
    }

    static PantsCloudStorageLocation CreateS3Location(string host, string prefix) => new(
        new PantsCloudProviderConfiguration.S3Compatible(
            "bucket",
            "us-test-1",
            new Uri($"https://{host}"),
            true,
            new PantsS3CredentialSource.StaticCredentials("access", "secret")),
        prefix);

    static PantsCloudStorageLocation CreateGcsLocation(string host, string prefix) => new(
        new PantsCloudProviderConfiguration.Gcs(
            "bucket",
            "project",
            new Uri($"https://{host}"),
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.BearerToken("token")),
        prefix);

    static PantsCloudStorageLocation CreateAzureLocation(string host, string prefix) => new(
        new PantsCloudProviderConfiguration.AzureBlob(
            "account",
            "container",
            new Uri($"https://{host}"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        prefix);

    static async Task DeleteAllAsync(PantsCloudStorageLocation location)
    {
        var store = CloudObjectStoreFactory.Create(location, TimeSpan.FromMinutes(1));
        var objectKeys = await store.ListAllAsync(string.Empty, CancellationToken.None);
        foreach (var objectKey in objectKeys)
        {
            _ = await store.DeleteAsync(
                objectKey,
                new CloudObjectDeleteCondition.Unconditional(),
                CancellationToken.None);
        }
    }
}
