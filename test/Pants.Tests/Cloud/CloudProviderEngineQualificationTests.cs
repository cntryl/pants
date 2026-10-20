using System.Collections.Concurrent;
using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudProviderEngineQualificationTests
{
    [Fact]
    public async Task ShouldBoundHeadAndRangedGetAttemptsDuringS3CacheLossRecovery()
    {
        using var handler = new InMemoryCloudProviderHandler();
        using var client = new HttpClient(handler);
        using var sourceCache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        var location = CreateS3Location("s3.example.test", "database");
        var topology = PantsCloudStorageTopology.Shared(location);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.CloudMulti(sourceCache.Path, topology)
            .WithBackgroundCompaction(false);
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'v');

        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), payload);
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var activities = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(activities);
        ActivityTraceId recoveryTraceId;
        IPantsDatabase recovered;
        using (var recovery = new Activity("CloudCacheLossRecovery").Start())
        {
            recoveryTraceId = recovery.TraceId;
            recovered = await PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.CloudMulti(replacementCache.Path, topology)
                    .WithBackgroundCompaction(false),
                dependencies);
        }

        await using (recovered)
        {
            await using var reader = await recovered.Transactions.BeginAsync(
                recovered.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var value = await reader.GetAsync("key"u8.ToArray());
            Assert.Equal(payload, Assert.IsType<ReadOnlyMemory<byte>>(value).ToArray());
        }

        var attempts = activities
            .Where(activity => activity.TraceId == recoveryTraceId &&
                               activity.DisplayName == "Pants.Cloud.HttpAttempt")
            .ToArray();
        var headCount = attempts.Count(activity =>
            Equals(activity.GetTagItem("pants.cloud_io.method"), "HEAD"));
        var rangedGetCount = attempts.Count(activity =>
            Equals(activity.GetTagItem("pants.cloud_io.method"), "GET") &&
            Equals(activity.GetTagItem("pants.cloud_io.range"), true));

        Assert.InRange(headCount, 1, 32);
        Assert.InRange(rangedGetCount, 1, 32);
    }

    [Fact]
    public async Task ShouldRecoverEngineFromS3CompatibleProtocolAfterLocalCacheLoss()
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
            new PantsAwsS3Provider(
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

    [LiveS3Fact]
    public async Task ShouldRecoverEngineFromLiveAwsS3AfterLocalCacheLoss()
    {
        var bucket = Environment.GetEnvironmentVariable("PANTS_LIVE_S3_BUCKET");
        var region = Environment.GetEnvironmentVariable("PANTS_LIVE_S3_REGION");
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException("Live S3 settings changed after test discovery.");
        }

        var location = new PantsCloudStorageLocation(
            new PantsAwsS3Provider(
                bucket,
                region,
                new PantsS3CredentialSource.AwsDefaultChain()),
            $"pants-qualification/{Guid.NewGuid():N}");
        try
        {
            await WriteFlushRecoverAsync(
                PantsCloudStorageTopology.Shared(location),
                cloudHttpClient: null);
        }
        finally
        {
            await using var store = await CloudObjectStoreFactory.CreateAsync(
                location,
                TimeSpan.FromSeconds(10));
            foreach (var key in await store.ListAllAsync(string.Empty, CancellationToken.None))
            {
                _ = await store.DeleteAsync(
                    key,
                    new PantsCloudObjectDeleteCondition.Unconditional(),
                    CancellationToken.None);
            }
        }
    }

    sealed class LiveS3FactAttribute : FactAttribute
    {
        public LiveS3FactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANTS_LIVE_S3_BUCKET")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANTS_LIVE_S3_REGION")))
            {
                Skip = "Set PANTS_LIVE_S3_BUCKET and PANTS_LIVE_S3_REGION to opt in to live S3 qualification.";
            }
        }
    }

    [Fact]
    public async Task ShouldRecoverEngineFromGcsJsonProtocolAfterLocalCacheLoss()
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
            new PantsGcsProvider(
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
    public async Task ShouldRouteThreeLocationTopologyThroughProviderProtocols()
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
    public async Task ShouldRouteTwoLocationTopologyThroughProviderProtocols()
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

    static async Task WriteFlushRecoverAsync(
        PantsCloudStorageTopology topology,
        HttpClient? cloudHttpClient)
    {
        using var sourceCache = new TemporaryDirectory();
        using var replacementCache = new TemporaryDirectory();
        var dependencies = new RuntimeDependencies(cloudHttpClient: cloudHttpClient);
        var options = PantsOpenOptions.CloudMulti(sourceCache.Path, topology)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.CloudMulti(replacementCache.Path, topology)
                .WithBackgroundCompaction(false),
            dependencies);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("key"u8.ToArray()))));
    }

    static ActivityListener CreateActivityListener(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Pants",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static PantsCloudStorageLocation CreateS3Location(string host, string prefix) => new(
        new PantsS3CompatibleProvider(
            "bucket",
            "us-test-1",
            new Uri($"https://{host}"),
            true,
            new PantsS3CredentialSource.StaticCredentials("access", "secret")),
        prefix);

    static PantsCloudStorageLocation CreateGcsLocation(string host, string prefix) => new(
        new PantsGcsProvider(
            "bucket",
            "project",
            new Uri($"https://{host}"),
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.BearerToken("token")),
        prefix);

    static PantsCloudStorageLocation CreateAzureLocation(string host, string prefix) => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri($"https://{host}"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        prefix);
}
