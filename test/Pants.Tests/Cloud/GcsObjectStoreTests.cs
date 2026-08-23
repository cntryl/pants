using System.Net;
using System.Net.Http.Headers;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class GcsObjectStoreTests
{
    [Fact]
    public async Task ShouldUseBearerTokenAndGenerationConditionsGivenJsonApi()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, "value", "7")
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

        var value = await store.GetAsync("metadata/manifest.json", CancellationToken.None);
        var replaced = await store.PutAsync(
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
        var store = CreateStore(client, PantsGcsApiStyle.Json, string.Empty);

        var exception = await Assert.ThrowsAsync<PantsIOException>(() => store.PutAsync(
            "metadata/manifest.json",
            "replacement"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("1"),
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
        var store = CreateStore(client, PantsGcsApiStyle.Json, string.Empty);

        var exception = await Assert.ThrowsAsync<PantsIOException>(() => store.DeleteAsync(
            "metadata/manifest.json",
            new CloudObjectDeleteCondition.IfVersion("1"),
            CancellationToken.None).AsTask());

        Assert.Contains("indeterminate", exception.Message, StringComparison.Ordinal);
        Assert.False(objectExists);
        Assert.Single(handler.Requests);
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

        var request = Assert.Single(handler.Requests);
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
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
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
        var credentialPath = Path.Combine(directory.Path, "authorized-user.json");
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
            : Response(HttpStatusCode.OK, "value", "1"));
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.Gcs(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Json,
                new PantsGcsCredentialSource.AuthorizedUserJsonFile(credentialPath)),
            "database");

        var store = CloudObjectStoreFactory.Create(
            location,
            TimeSpan.FromSeconds(5),
            client);
        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.IsType<GcsObjectStore>(store);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer refreshed-token", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task ShouldFollowEveryGcsJsonListPageUsingOpaquePageToken()
    {
        var page = 0;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++page == 1
                ? """
                  {
                    "items": [{ "name": "database/sst/0001.sst" }],
                    "nextPageToken": "opaque+/ token"
                  }
                  """
                : """
                  { "items": [{ "name": "database/sst/0002.sst" }] }
                  """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Json, "database");

        var objectKeys = await store.ListAllAsync("sst/", CancellationToken.None);

        Assert.Equal(["sst/0001.sst", "sst/0002.sst"], objectKeys);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("prefix=database%2Fsst%2F", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("pageToken=opaque%2B%2F%20token", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectRepeatedGcsJsonListPageToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"nextPageToken\": \"repeat\" }")
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Json, string.Empty);

        var exception =
            await Assert.ThrowsAsync<PantsInternalException>(() =>
                store.ListAllAsync("sst/", CancellationToken.None).AsTask());

        Assert.Contains("repeated continuation token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ShouldFollowEveryGcsXmlListPageUsingLastKeyMarkerFallback()
    {
        var page = 0;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++page == 1
                ? """
                  <ListBucketResult>
                    <Contents><Key>database/sst/0001.sst</Key></Contents>
                    <IsTruncated>true</IsTruncated>
                  </ListBucketResult>
                  """
                : """
                  <ListBucketResult>
                    <Contents><Key>database/sst/0002.sst</Key></Contents>
                    <IsTruncated>false</IsTruncated>
                  </ListBucketResult>
                  """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Xml, "database");

        var objectKeys = await store.ListAllAsync("sst/", CancellationToken.None);

        Assert.Equal(["sst/0001.sst", "sst/0002.sst"], objectKeys);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            "marker=database%2Fsst%2F0001.sst",
            handler.Requests[1].Uri.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldReadGcsJsonMetadataAndUseGenerationForConditionalDelete()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{ \"size\": \"17\", \"etag\": \"etag-17\", \"generation\": \"17\" }")
            }
            : GcsJsonPredicateFailure("conditionNotMet"));
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Json, string.Empty);

        var metadata = await store.HeadAsync("sst/object.sst", CancellationToken.None);
        var outcome = await store.DeleteAsync(
            "sst/object.sst",
            new CloudObjectDeleteCondition.IfVersion("17"),
            CancellationToken.None);

        Assert.Equal(17UL, Assert.IsType<CloudObjectMetadata>(metadata).SizeBytes);
        Assert.Equal("etag-17", metadata.ETag);
        Assert.Equal("17", metadata.Generation);
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet, outcome);
        Assert.Contains("ifGenerationMatch=17", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldFailClosedGivenGcsRetentionPolicyResponseToConditionalDelete()
    {
        var handler = new RecordingHandler(_ => GcsJsonPredicateFailure("retentionPolicyNotMet"));
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Json, string.Empty);

        await Assert.ThrowsAsync<PantsIOException>(() => store.DeleteAsync(
            "sst/object.sst",
            new CloudObjectDeleteCondition.IfVersion("17"),
            CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldReadGcsXmlMetadataAndUseGenerationHeaderForConditionalDelete()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Head
            ? XmlMetadataResponse(19, "\"etag-19\"", "19")
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent(
                    "<Error><Code>PreconditionFailed</Code></Error>")
            });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, PantsGcsApiStyle.Xml, string.Empty);

        var metadata = await store.HeadAsync("sst/object.sst", CancellationToken.None);
        var outcome = await store.DeleteAsync(
            "sst/object.sst",
            new CloudObjectDeleteCondition.IfVersion("19"),
            CancellationToken.None);

        Assert.Equal(19UL, Assert.IsType<CloudObjectMetadata>(metadata).SizeBytes);
        Assert.Equal("\"etag-19\"", metadata.ETag);
        Assert.Equal("19", metadata.Generation);
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet, outcome);
        Assert.Equal("19", handler.Requests[1].GenerationMatch);
    }

    static GcsObjectStore CreateStore(
        HttpClient client,
        PantsGcsApiStyle apiStyle,
        string prefix) => new(
        new PantsCloudProviderConfiguration.Gcs(
            "bucket",
            "project",
            new Uri("https://gcs.example.test"),
            apiStyle,
            apiStyle == PantsGcsApiStyle.Json
                ? new PantsGcsCredentialSource.BearerToken("token")
                : new PantsGcsCredentialSource.HmacKey("access", "secret")),
        prefix,
        client,
        TimeSpan.FromSeconds(5));

    static HttpResponseMessage GcsJsonPredicateFailure(string reason) => new(
        HttpStatusCode.PreconditionFailed)
    {
        Content = new StringContent(
            $$"""
              { "error": { "errors": [{ "reason": "{{reason}}" }] } }
              """)
    };

    static HttpResponseMessage XmlMetadataResponse(
        int size,
        string etag,
        string generation)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[size]),
            Headers = { ETag = new EntityTagHeaderValue(etag) }
        };
        response.Headers.TryAddWithoutValidation("x-goog-generation", generation);
        return response;
    }

    static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        string generation)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(TestBytes.FromString(content)),
            Headers = { ETag = new EntityTagHeaderValue("\"etag\"") }
        };
        response.Headers.TryAddWithoutValidation("x-goog-generation", generation);
        return response;
    }

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
                request.Headers.Authorization?.ToString(),
                request.Headers.IfNoneMatch.SingleOrDefault()?.Tag,
                request.Headers.TryGetValues("x-goog-if-generation-match", out var generations)
                    ? generations.Single()
                    : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IfNoneMatch,
        string? GenerationMatch);
}
