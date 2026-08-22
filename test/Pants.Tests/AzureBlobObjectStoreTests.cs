using System.Net;

namespace Pants.Tests;

public sealed class AzureBlobObjectStoreTests
{
    [Fact]
    public async Task ShouldUseSasAndConditionalHeadersGivenDirectHttpObjectWrites()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("lease"u8.ToArray()),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"") }
            }
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        using var client = new HttpClient(handler);
        var store = new AzureBlobObjectStore(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test/account"),
                new PantsAzureCredentialSource.SasToken("sig=secret-token")),
            "database-a",
            client,
            TimeSpan.FromSeconds(5));

        CloudObject? value = await store.GetAsync(
            PantsCloudObjectLayout.LeaseObjectKey,
            CancellationToken.None);
        bool replaced = await store.PutAsync(
            PantsCloudObjectLayout.LeaseObjectKey,
            "next"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("\"v1\""),
            CancellationToken.None);

        Assert.NotNull(value);
        Assert.Equal("lease", TestBytes.ToText(value.Data));
        Assert.Equal("\"v1\"", value.Version);
        Assert.False(replaced);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("sig=secret-token", request.Uri.Query.TrimStart('?'));
            Assert.Equal(
                "/account/container/database-a/midge_primary_lease.json",
                request.Uri.AbsolutePath);
        });
        Assert.Equal("\"v1\"", handler.Requests[1].IfMatch);
    }

    [Fact]
    public async Task ShouldSignSharedKeyWithoutDisclosingCredential()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        const string secret = "c2VjcmV0LWtleQ==";
        var store = new AzureBlobObjectStore(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                null,
                new PantsAzureCredentialSource.SharedKey(secret)),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        Assert.True(await store.PutAsync(
            "metadata/manifest.json",
            "{}"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.StartsWith("SharedKey account:", request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, request.Authorization, StringComparison.Ordinal);
        Assert.Equal("*", request.IfNoneMatch);
    }

    [Fact]
    public void ShouldSelectAzureClientGivenProviderLocation()
    {
        using var client = new HttpClient(new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=value")),
            "database");

        ICloudObjectStore store = CloudObjectStoreFactory.Create(
            location,
            TimeSpan.FromSeconds(5),
            client);

        Assert.IsType<AzureBlobObjectStore>(store);
    }

    [Fact]
    public async Task ShouldRetryTransientFailuresWithinOperationDeadline()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ => ++attempts < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("value"u8.ToArray()),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"") }
            });
        using var client = new HttpClient(handler);
        var store = new AzureBlobObjectStore(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=value")),
            "database",
            client,
            TimeSpan.FromSeconds(5));

        var value = await store.GetAsync("object", CancellationToken.None);

        Assert.NotNull(value);
        Assert.Equal(3, attempts);
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
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.IfMatch.SingleOrDefault()?.Tag,
                request.Headers.IfNoneMatch.SingleOrDefault()?.Tag));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IfMatch,
        string? IfNoneMatch);
}
