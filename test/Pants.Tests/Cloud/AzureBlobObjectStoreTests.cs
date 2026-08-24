using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class AzureBlobObjectStoreTests
{
    [Fact]
    public async Task ShouldUseSasAndConditionalHeadersGivenDirectHttpObjectWrites()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("lease"u8.ToArray()),
                Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
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

        var value = await store.GetAsync(
            PantsCloudObjectLayout.LeaseObjectKey,
            CancellationToken.None);
        var replaced = await store.PutAsync(
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

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("SharedKey account:", request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, request.Authorization, StringComparison.Ordinal);
        Assert.Equal("*", request.IfNoneMatch);
    }

    [Fact]
    public async Task ShouldSignConditionalSharedKeyFieldsWithExactHmac()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        const string account = "account";
        const string secret = "c2VjcmV0LWtleQ==";
        var store = new AzureBlobObjectStore(
            new PantsCloudProviderConfiguration.AzureBlob(
                account,
                "container",
                null,
                new PantsAzureCredentialSource.SharedKey(secret)),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        Assert.True(await store.PutAsync(
            "metadata/create.json",
            "create"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));
        Assert.True(await store.PutAsync(
            "metadata/update.json",
            "update"u8.ToArray(),
            new CloudObjectWriteCondition.IfVersion("\"v1\""),
            CancellationToken.None));

        Assert.Collection(
            handler.Requests,
            create =>
            {
                Assert.Null(create.IfMatch);
                Assert.Equal("*", create.IfNoneMatch);
                Assert.Equal("BlockBlob", create.BlobType);
                Assert.Equal("2024-11-04", create.AzureVersion);
                Assert.NotNull(create.AzureDate);
                Assert.Equal(
                    CreateExpectedSharedKeyAuthorization(create, account, secret),
                    create.Authorization);
            },
            update =>
            {
                Assert.Equal("\"v1\"", update.IfMatch);
                Assert.Null(update.IfNoneMatch);
                Assert.Equal("BlockBlob", update.BlobType);
                Assert.Equal("2024-11-04", update.AzureVersion);
                Assert.NotNull(update.AzureDate);
                Assert.Equal(
                    CreateExpectedSharedKeyAuthorization(update, account, secret),
                    update.Authorization);
            });
    }

    [Fact]
    public void ShouldSelectAzureClientGivenProviderLocation()
    {
        using var client = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=value")),
            "database");

        var store = CloudObjectStoreFactory.Create(
            location,
            TimeSpan.FromSeconds(5),
            client);

        Assert.IsType<AzureBlobObjectStore>(store);
    }

    [Fact]
    public void ShouldJoinPinnedCanonicalHeadersDirectlyToAzureResource()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://127.0.0.1:10000/devstoreaccount1/container/blob");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Sun, 10 Aug 2026 12:00:00 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2024-11-04");

        var value = AzureBlobObjectStore.CreateSharedKeyStringToSign(
            request,
            "devstoreaccount1");

        Assert.Contains(
            "x-ms-version:2024-11-04\n/devstoreaccount1/devstoreaccount1/container/blob",
            value,
            StringComparison.Ordinal);
        Assert.DoesNotContain("x-ms-version:2024-11-04\n\n/", value, StringComparison.Ordinal);
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
                Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
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

    [Fact]
    public async Task ShouldFollowEveryAzureBlobListPageUsingOpaqueMarker()
    {
        var page = 0;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++page == 1
                ? """
                  <EnumerationResults>
                    <Blobs><Blob><Name>database/sst/0001.sst</Name></Blob></Blobs>
                    <NextMarker>opaque+/ marker</NextMarker>
                  </EnumerationResults>
                  """
                : """
                  <EnumerationResults>
                    <Blobs><Blob><Name>database/sst/0002.sst</Name></Blob></Blobs>
                    <NextMarker />
                  </EnumerationResults>
                  """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, "database");

        var objectKeys = await store.ListAllAsync("sst/", CancellationToken.None);

        Assert.Equal(["sst/0001.sst", "sst/0002.sst"], objectKeys);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("restype=container", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("comp=list", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("prefix=database%2Fsst%2F", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("marker=opaque%2B%2F%20marker", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectAzureListKeyOutsideConfiguredPrefix()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <EnumerationResults>
                  <Blobs>
                    <Blob><Name>database/sst/inside.sst</Name></Blob>
                    <Blob><Name>foreign/sst/outside.sst</Name></Blob>
                  </Blobs>
                  <NextMarker />
                </EnumerationResults>
                """)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(client, "database");

        var exception = await Assert.ThrowsAsync<PantsIOException>(() =>
            store.ListAllAsync("sst/", CancellationToken.None).AsTask());

        Assert.Contains("foreign/sst/outside.sst", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldReadAzureBlobHeadMetadataAndApplyConditionalDelete()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[9]),
                Headers = { ETag = new EntityTagHeaderValue("\"v9\"") }
            }
            : AzurePredicateFailure());
        using var client = new HttpClient(handler);
        var store = CreateStore(client, string.Empty);

        var metadata = await store.HeadAsync("sst/object.sst", CancellationToken.None);
        var outcome = await store.DeleteAsync(
            "sst/object.sst",
            new CloudObjectDeleteCondition.IfVersion("\"v9\""),
            CancellationToken.None);

        Assert.Equal(9UL, Assert.IsType<CloudObjectMetadata>(metadata).SizeBytes);
        Assert.Equal("\"v9\"", metadata.ETag);
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet, outcome);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("\"v9\"", handler.Requests[1].IfMatch);
    }

    static AzureBlobObjectStore CreateStore(HttpClient client, string prefix) => new(
        new PantsCloudProviderConfiguration.AzureBlob(
            "account",
            "container",
            new Uri("https://storage.example.test/account"),
            new PantsAzureCredentialSource.SasToken("sig=secret-token")),
        prefix,
        client,
        TimeSpan.FromSeconds(5));

    static HttpResponseMessage AzurePredicateFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent(
                "<Error><Code>ConditionNotMet</Code></Error>")
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", "ConditionNotMet");
        return response;
    }

    static string CreateExpectedSharedKeyAuthorization(
        RecordedRequest request,
        string account,
        string base64Key)
    {
        var stringToSign = CreateExpectedSharedKeyStringToSign(request, account);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(
            Convert.FromBase64String(base64Key),
            Encoding.UTF8.GetBytes(stringToSign)));
        return $"SharedKey {account}:{signature}";
    }

    static string CreateExpectedSharedKeyStringToSign(
        RecordedRequest request,
        string account)
    {
        var contentLength = request.ContentLength is > 0
            ? request.ContentLength.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        return string.Join(
            '\n',
            request.Method.Method,
            string.Empty,
            string.Empty,
            contentLength,
            string.Empty,
            request.ContentType ?? string.Empty,
            string.Empty,
            string.Empty,
            request.IfMatch ?? string.Empty,
            request.IfNoneMatch ?? string.Empty,
            string.Empty,
            string.Empty,
            $"x-ms-blob-type:{request.BlobType}\nx-ms-date:{request.AzureDate}\nx-ms-version:{request.AzureVersion}\n/{account}{request.Uri.AbsolutePath}");
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
                request.Headers.IfMatch.SingleOrDefault()?.Tag,
                request.Headers.IfNoneMatch.SingleOrDefault()?.Tag,
                request.Content?.Headers.ContentLength,
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.TryGetValues("x-ms-blob-type", out var blobTypes)
                    ? blobTypes.Single()
                    : null,
                request.Headers.TryGetValues("x-ms-date", out var dates)
                    ? dates.Single()
                    : null,
                request.Headers.TryGetValues("x-ms-version", out var versions)
                    ? versions.Single()
                    : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IfMatch,
        string? IfNoneMatch,
        long? ContentLength,
        string? ContentType,
        string? BlobType,
        string? AzureDate,
        string? AzureVersion);
}
