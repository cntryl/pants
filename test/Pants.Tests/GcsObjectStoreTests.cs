using System.Net;

namespace Pants.Tests;

public sealed class GcsObjectStoreTests
{
    [Fact]
    public async Task ShouldUseBearerTokenAndGenerationConditionsGivenJsonApi()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, "value", generation: "7")
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        using var client = new HttpClient(handler);
        var store = new GcsObjectStore(
            new PantsCloudProviderConfiguration.Gcs(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Json,
                new PantsGcsCredentialSource.BearerToken("secret-token")),
            "database",
            client,
            TimeSpan.FromSeconds(5));

        CloudObject? value = await store.GetAsync("metadata/manifest.json", CancellationToken.None);
        bool replaced = await store.PutAsync(
            "metadata/manifest.json",
            "next"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("7"),
            CancellationToken.None);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<CloudObject>(value).Data));
        Assert.Equal("7", value.Version);
        Assert.False(replaced);
        Assert.Equal(
            "/storage/v1/b/bucket/o/database%2Fmetadata%2Fmanifest.json",
            handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("alt=media", handler.Requests[0].Uri.Query.TrimStart('?'));
        Assert.Contains("ifGenerationMatch=7", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.All(handler.Requests, static request => Assert.Equal(
            "Bearer secret-token",
            request.Authorization));
    }

    [Fact]
    public async Task ShouldSignXmlApiWithGoog1HmacWithoutDisclosingSecret()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        const string secret = "never-render-this-secret";
        var store = new GcsObjectStore(
            new PantsCloudProviderConfiguration.Gcs(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Xml,
                new PantsGcsCredentialSource.HmacKey("access-id", secret)),
            "database",
            client,
            TimeSpan.FromSeconds(5));

        Assert.True(await store.PutAsync(
            "metadata/manifest.json",
            "{}"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/bucket/database/metadata/manifest.json", request.Uri.AbsolutePath);
        Assert.StartsWith("GOOG1 access-id:", request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, request.Authorization, StringComparison.Ordinal);
        Assert.Equal("0", request.GenerationMatch);
    }

    [Fact]
    public void ShouldMatchPinnedMidgeGoog1CanonicalSignature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "https://storage.googleapis.com/bucket/lease/primary")
        {
            Content = new ByteArrayContent("lease"u8.ToArray())
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Goog-Meta-Owner", " first   holder ");
        request.Headers.TryAddWithoutValidation("x-goog-if-generation-match", "42");
        request.Headers.TryAddWithoutValidation("x-goog-meta-owner", "second holder");

        var authorization = GcsObjectStore.CreateGoog1Authorization(
            request,
            "GOOG123",
            "c2VjcmV0",
            "Sat, 08 Aug 2026 12:00:00 GMT");

        Assert.Equal("GOOG1 GOOG123:rkS054NuU5pCTrAsV8U7cjpRaOY=", authorization);
    }

    [Fact]
    public async Task ShouldRefreshAuthorizedUserCredentialAndRouteGcsFactoryVariant()
    {
        using var directory = new TemporaryDirectory();
        string credentialPath = Path.Combine(directory.Path, "authorized-user.json");
        File.WriteAllText(
            credentialPath,
            """
            {
              "type": "authorized_user",
              "client_id": "client",
              "client_secret": "secret",
              "refresh_token": "refresh",
              "token_uri": "https://oauth.example.test/token"
            }
            """);
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "oauth.example.test"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"refreshed-token\",\"expires_in\":3600}")
            }
            : Response(HttpStatusCode.OK, "value", generation: "1"));
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.Gcs(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Json,
                new PantsGcsCredentialSource.AuthorizedUserJsonFile(credentialPath)),
            "database");

        ICloudObjectStore store = CloudObjectStoreFactory.Create(
            location,
            TimeSpan.FromSeconds(5),
            client);
        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.IsType<GcsObjectStore>(store);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer refreshed-token", handler.Requests[1].Authorization);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        string generation)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(TestBytes.FromString(content)),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"") }
        };
        response.Headers.TryAddWithoutValidation("x-goog-generation", generation);
        return response;
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
                request.Headers.IfNoneMatch.SingleOrDefault()?.Tag,
                request.Headers.TryGetValues("x-goog-if-generation-match", out var generations)
                    ? generations.Single()
                    : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IfNoneMatch,
        string? GenerationMatch);
}
