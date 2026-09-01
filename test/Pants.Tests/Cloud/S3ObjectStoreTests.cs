using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class S3ObjectStoreTests
{
    const string EmptyPayloadSha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task ShouldSignAndConditionPathStyleS3RequestsWithoutDisclosingSecrets()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("value"u8.ToArray()),
                Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
            }
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        using var client = new HttpClient(handler);
        const string secret = "do-not-log-this-secret";
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.S3Compatible(
                "bucket",
                "us-test-1",
                new Uri("https://objects.example.test/base"),
                true,
                new PantsS3CredentialSource.StaticCredentials("access", secret, "session")),
            "database",
            client,
            TimeSpan.FromSeconds(5));

        var value = await store.GetAsync("metadata/manifest.json", CancellationToken.None);
        var replaced = await store.PutAsync(
            "metadata/manifest.json",
            "next"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("\"v1\""),
            CancellationToken.None);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<CloudObject>(value).Data));
        Assert.False(replaced);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(
                "/base/bucket/database/metadata/manifest.json",
                request.Uri.AbsolutePath);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=access/", request.Authorization);
            Assert.DoesNotContain(secret, request.Authorization, StringComparison.Ordinal);
            Assert.Equal("session", request.SecurityToken);
        });
        Assert.Equal("\"v1\"", handler.Requests[1].IfMatch);
    }

    [Fact]
    public async Task ShouldRetryTransientS3ResponsesWithinTheOperationDeadline()
    {
        var responses = new Queue<HttpStatusCode>(
            [HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(responses.Dequeue())
        {
            Content = new ByteArrayContent("value"u8.ToArray()),
            Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
        });
        using var client = new HttpClient(handler);
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.AwsS3(
                "bucket",
                "us-east-1",
                new PantsS3CredentialSource.StaticCredentials("access", "secret")),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ShouldNotRetryConditionalPutGivenCommittedMutationReturnsServiceUnavailable()
    {
        var mutationCommitted = false;
        var handler = new RecordingHandler(_ =>
        {
            if (!mutationCommitted)
            {
                mutationCommitted = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, string.Empty);

        var exception = await Assert.ThrowsAsync<PantsIOException>(() => store.PutAsync(
            "metadata/manifest.json",
            "replacement"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("\"v1\""),
            CancellationToken.None).AsTask());

        Assert.Contains("indeterminate", exception.Message, StringComparison.Ordinal);
        Assert.True(mutationCommitted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ShouldNotRetryConditionalDeleteGivenCommittedMutationLosesResponse()
    {
        var objectExists = true;
        var handler = new RecordingHandler(_ =>
        {
            if (objectExists)
            {
                objectExists = false;
                throw new HttpRequestException("response lost after delete commit");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, string.Empty);

        var exception = await Assert.ThrowsAsync<PantsIOException>(() => store.DeleteAsync(
            "metadata/manifest.json",
            new CloudObjectDeleteCondition.IfVersion("\"v1\""),
            CancellationToken.None).AsTask());

        Assert.Contains("indeterminate", exception.Message, StringComparison.Ordinal);
        Assert.False(objectExists);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(
        "database.example",
        "https://s3.us-east-1.amazonaws.com/database.example/object",
        "s3.us-east-1.amazonaws.com")]
    [InlineData(
        "database",
        "https://database.s3.us-east-1.amazonaws.com/object",
        "database.s3.us-east-1.amazonaws.com")]
    public async Task ShouldSelectNativeAwsBucketUriAndSignItsExactPath(
        string bucket,
        string expectedUri,
        string expectedHost)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("value"u8.ToArray()),
            Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
        });
        using var client = new HttpClient(handler);
        const string accessKey = "access";
        const string secretKey = "secret";
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.AwsS3(
                bucket,
                "us-east-1",
                new PantsS3CredentialSource.StaticCredentials(accessKey, secretKey)),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedUri, request.Uri.AbsoluteUri);
        Assert.Equal(
            CreateExpectedAwsAuthorization(request, expectedHost, accessKey, secretKey),
            request.Authorization);
    }

    [Fact]
    public async Task ShouldApplyOperationDeadlineWhileReadingResponseBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new NeverCompletingHttpContent(),
            Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
        });
        using var client = new HttpClient(handler);
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.AwsS3(
                "bucket",
                "us-east-1",
                new PantsS3CredentialSource.StaticCredentials("access", "secret")),
            string.Empty,
            client,
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<PantsTimeoutException>(() =>
            store.GetAsync("object", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldFollowEveryS3ListPageUsingOpaqueContinuationToken()
    {
        var page = 0;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++page == 1
                ? """
                  <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                    <Contents><Key>database/sst/0001.sst</Key></Contents>
                    <IsTruncated>true</IsTruncated>
                    <NextContinuationToken>token/with spaces</NextContinuationToken>
                  </ListBucketResult>
                  """
                : """
                  <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                    <Contents><Key>database/sst/0002.sst</Key></Contents>
                    <IsTruncated>false</IsTruncated>
                  </ListBucketResult>
                  """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, "database");

        var objectKeys = await store.ListAllAsync("sst/", CancellationToken.None);

        Assert.Equal(["sst/0001.sst", "sst/0002.sst"], objectKeys);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("list-type=2", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("prefix=database%2Fsst%2F", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains(
            "continuation-token=token%2Fwith%20spaces",
            handler.Requests[1].Uri.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectS3ListKeyOutsideConfiguredPrefix()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <ListBucketResult>
                  <Contents><Key>database/sst/inside.sst</Key></Contents>
                  <Contents><Key>foreign/sst/outside.sst</Key></Contents>
                  <IsTruncated>false</IsTruncated>
                </ListBucketResult>
                """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, "database");

        var exception = await Assert.ThrowsAsync<PantsIOException>(() =>
            store.ListAllAsync("sst/", CancellationToken.None).AsTask());

        Assert.Contains("foreign/sst/outside.sst", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectRepeatedS3ListContinuationToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                <ListBucketResult>
                  <IsTruncated>true</IsTruncated>
                  <NextContinuationToken>repeat</NextContinuationToken>
                </ListBucketResult>
                """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, string.Empty);

        var exception =
            await Assert.ThrowsAsync<PantsInternalException>(() =>
                store.ListAllAsync("sst/", CancellationToken.None).AsTask());

        Assert.Contains("repeated continuation token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ShouldReadS3HeadMetadataAndApplyConditionalDelete()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[12]),
                Headers = { ETag = new EntityTagHeaderValue("\"v12\"") }
            }
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent(
                    "<Error><Code>PreconditionFailed</Code></Error>")
            });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, string.Empty);

        var metadata = await store.HeadAsync("sst/object.sst", CancellationToken.None);
        var outcome = await store.DeleteAsync(
            "sst/object.sst",
            new CloudObjectDeleteCondition.IfVersion("\"v12\""),
            CancellationToken.None);

        Assert.Equal(12UL, Assert.IsType<CloudObjectMetadata>(metadata).SizeBytes);
        Assert.Equal("\"v12\"", metadata.ETag);
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet, outcome);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("\"v12\"", handler.Requests[1].IfMatch);
    }

    [Fact]
    public void ShouldResolveSharedProfileAndRouteEveryS3ProviderVariant()
    {
        using var directory = new TemporaryDirectory();
        var credentialsPath = Path.Combine(directory.Path, "credentials");
        File.WriteAllText(
            credentialsPath,
            "[qualification]\naws_access_key_id = profile-access\naws_secret_access_key = profile-secret\n");
        using var client = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        PantsCloudProviderConfiguration[] providers =
        [
            new PantsCloudProviderConfiguration.AwsS3(
                "bucket",
                "us-east-1",
                new PantsS3CredentialSource.SharedProfile("qualification", credentialsPath)),
            new PantsCloudProviderConfiguration.S3Compatible(
                "bucket",
                "us-east-1",
                new Uri("https://objects.example.test"),
                true,
                new PantsS3CredentialSource.SharedProfile("qualification", credentialsPath))
        ];

        Assert.All(providers, provider => Assert.IsType<S3ObjectStore>(
            CloudObjectStoreFactory.Create(
                new PantsCloudStorageLocation(provider, "prefix"),
                TimeSpan.FromSeconds(5),
                client)));
    }

    static S3ObjectStore CreateStore(HttpClient client, string prefix) => new(
        new PantsCloudProviderConfiguration.S3Compatible(
            "bucket",
            "us-test-1",
            new Uri("https://objects.example.test/base"),
            true,
            new PantsS3CredentialSource.StaticCredentials("access", "secret")),
        prefix,
        client,
        TimeSpan.FromSeconds(5));

    static string CreateExpectedAwsAuthorization(
        RecordedRequest request,
        string host,
        string accessKey,
        string secretKey)
    {
        var timestamp = Assert.IsType<string>(request.AmzDate);
        Assert.Equal(EmptyPayloadSha256, request.ContentSha256);
        var date = timestamp[..8];
        var canonicalHeaders =
            $"host:{host}\nx-amz-content-sha256:{EmptyPayloadSha256}\nx-amz-date:{timestamp}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            request.Uri.AbsolutePath,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            EmptyPayloadSha256);
        var scope = $"{date}/us-east-1/s3/aws4_request";
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var regionKey = Hmac(dateKey, "us-east-1");
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            signingKey,
            Encoding.UTF8.GetBytes(stringToSign)));
        return
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("x-amz-security-token", out var tokens)
                    ? tokens.Single()
                    : null,
                request.Headers.IfMatch.SingleOrDefault()?.Tag,
                request.Headers.TryGetValues("x-amz-date", out var dates)
                    ? dates.Single()
                    : null,
                request.Headers.TryGetValues("x-amz-content-sha256", out var contentHashes)
                    ? contentHashes.Single()
                    : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string Authorization,
        string? SecurityToken,
        string? IfMatch,
        string? AmzDate,
        string? ContentSha256);
}
