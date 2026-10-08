using System.Net;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudConditionalMutationClassificationTests
{
    static readonly PantsCloudObjectWriteCondition.IfVersion IfVersion = new("\"v1\"");

    [Fact]
    public async Task ShouldRetryS3ConditionalRequestConflictThenSucceed()
    {
        var attempt = 0;
        using var handler = new CountingHandler(_ => ++attempt < 3
            ? S3Error(HttpStatusCode.Conflict, "ConditionalRequestConflict")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var written = await CreateS3(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None);

        Assert.True(written);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ShouldReportLostConditionAfterS3ConditionalRequestConflictExhaustsRetries()
    {
        using var handler = new CountingHandler(_ => S3Error(HttpStatusCode.Conflict, "ConditionalRequestConflict"));
        using var client = new HttpClient(handler);

        var written = await CreateS3(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None);

        Assert.False(written);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task ShouldFailRatherThanReportConflictWhenS3OperationAbortedExhaustsRetries()
    {
        using var handler = new CountingHandler(_ => S3Error(HttpStatusCode.Conflict, "OperationAborted"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await CreateS3(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None));
        Assert.Equal(4, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "AccessDenied")]
    [InlineData(HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou")]
    public async Task ShouldFailOnS3PreconditionOrConflictStatusWithoutThePredicateCode(
        HttpStatusCode status,
        string code)
    {
        using var handler = new CountingHandler(_ => S3Error(status, code));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await CreateS3(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "LeaseIdMissing")]
    [InlineData(HttpStatusCode.Conflict, "SnapshotsPresent")]
    public async Task ShouldFailOnAzureStatusesThatAreNotAConditionFailure(HttpStatusCode status, string code)
    {
        using var handler = new CountingHandler(_ => AzureError(status, code));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await CreateAzure(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "ConditionNotMet")]
    [InlineData(HttpStatusCode.PreconditionFailed, "TargetConditionNotMet")]
    public async Task ShouldReportLostConditionOnAzurePredicateCodes(HttpStatusCode status, string code)
    {
        using var handler = new CountingHandler(_ => AzureError(status, code));
        using var client = new HttpClient(handler);

        Assert.False(await CreateAzure(client).PutAsync(
            "key",
            "v"u8.ToArray(),
            IfVersion,
            CancellationToken.None));
    }

    [Fact]
    public async Task ShouldReportExistingBlobAsLostConditionOnlyForCreateIfAbsent()
    {
        using var handler = new CountingHandler(_ => AzureError(HttpStatusCode.Conflict, "BlobAlreadyExists"));
        using var client = new HttpClient(handler);
        var store = CreateAzure(client);

        Assert.False(await store.PutAsync(
            "key",
            "v"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await store.PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldFailOnGcsPreconditionFailedForAnUnrelatedReason()
    {
        using var handler = new CountingHandler(_ => GcsError(HttpStatusCode.PreconditionFailed, "orgPolicyConstraintFailed"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await CreateGcs(client).PutAsync("key", "v"u8.ToArray(), IfVersion, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldReportLostConditionOnGcsConditionNotMet()
    {
        using var handler = new CountingHandler(_ => GcsError(HttpStatusCode.PreconditionFailed, "conditionNotMet"));
        using var client = new HttpClient(handler);

        Assert.False(await CreateGcs(client).PutAsync(
            "key",
            "v"u8.ToArray(),
            IfVersion,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("gcs")]
    public async Task ShouldSurfaceAnExhaustedHttp408OnReadsAsATimeout(string provider)
    {
        using var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestTimeout));
        using var client = new HttpClient(handler);
        ICloudObjectStore store = provider switch
        {
            "s3" => CreateS3(client),
            "azure" => CreateAzure(client),
            _ => CreateGcs(client)
        };

        await Assert.ThrowsAsync<PantsTimeoutException>(async () =>
            await store.GetAsync("key", CancellationToken.None));
    }

    static S3ObjectStore CreateS3(HttpClient client) => new(
        new PantsS3CompatibleProvider(
            "bucket",
            "us-test-1",
            new Uri("https://objects.example.test/base"),
            true,
            new PantsS3CredentialSource.StaticCredentials("access", "secret")),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static AzureBlobObjectStore CreateAzure(HttpClient client) => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test/account"),
            new PantsAzureCredentialSource.SasToken("sig=secret-token")),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static GcsObjectStore CreateGcs(HttpClient client) => new(
        new PantsGcsProvider(
            "bucket",
            "project",
            new Uri("https://gcs.example.test"),
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.BearerToken("token")),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static HttpResponseMessage S3Error(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($"<Error><Code>{code}</Code></Error>")
    };

    static HttpResponseMessage AzureError(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    static HttpResponseMessage GcsError(HttpStatusCode status, string reason) => new(status)
    {
        Content = new StringContent($"{{\"error\":{{\"errors\":[{{\"reason\":\"{reason}\"}}]}}}}")
    };

    sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(respond(request));
        }
    }
}
