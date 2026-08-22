using System.Net;

namespace Pants.Tests;

public sealed class S3ObjectStoreTests
{
    [Fact]
    public async Task ShouldSignAndConditionPathStyleS3RequestsWithoutDisclosingSecrets()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("value"u8.ToArray()),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"") }
            }
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        using var client = new HttpClient(handler);
        const string secret = "do-not-log-this-secret";
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.S3Compatible(
                "bucket",
                "us-test-1",
                new Uri("https://objects.example.test/base"),
                PathStyle: true,
                new PantsS3CredentialSource.StaticCredentials("access", secret, "session")),
            "database",
            client,
            TimeSpan.FromSeconds(5));

        CloudObject? value = await store.GetAsync("metadata/manifest.json", CancellationToken.None);
        bool replaced = await store.PutAsync(
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
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"") }
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
    public void ShouldResolveSharedProfileAndRouteEveryS3ProviderVariant()
    {
        using var directory = new TemporaryDirectory();
        string credentialsPath = Path.Combine(directory.Path, "credentials");
        File.WriteAllText(
            credentialsPath,
            "[qualification]\naws_access_key_id = profile-access\naws_secret_access_key = profile-secret\n");
        using var client = new HttpClient(new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));
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
                PathStyle: true,
                new PantsS3CredentialSource.SharedProfile("qualification", credentialsPath))
        ];

        Assert.All(providers, provider => Assert.IsType<S3ObjectStore>(
            CloudObjectStoreFactory.Create(
                new PantsCloudStorageLocation(provider, "prefix"),
                TimeSpan.FromSeconds(5),
                client)));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("x-amz-security-token", out var tokens)
                    ? tokens.Single()
                    : null,
                request.Headers.IfMatch.SingleOrDefault()?.Tag));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string Authorization,
        string? SecurityToken,
        string? IfMatch);
}
