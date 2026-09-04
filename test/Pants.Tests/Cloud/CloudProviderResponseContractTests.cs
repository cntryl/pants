using System.Net;
using System.Net.Http.Headers;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudProviderResponseContractTests
{
    public static TheoryData<string, string[], bool, bool> InvalidLengths => CreateInvalidLengths();

    static TheoryData<string, string[], bool, bool> CreateInvalidLengths()
    {
        var data = new TheoryData<string, string[], bool, bool>();
        foreach (var provider in new[] { "s3", "azure", "gcs-xml", "gcs-json" })
        {
            foreach (var headers in new string[][]
                     {
                         ["2"], ["4"], ["invalid"], ["-1"], ["+3"], ["18446744073709551616"], ["3", "4"]
                     })
            {
                foreach (var knownLength in new[] { false, true })
                {
                    data.Add(provider, headers, false, knownLength);
                    data.Add(provider, headers, true, knownLength);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidLengths))]
    public async Task ShouldRejectInvalidDeclaredGetLengths(
        string provider,
        string[] declaredLengths,
        bool ranged,
        bool knownLength)
    {
        using var handler = new ResponseHandler(declaredLengths, ranged, knownLength);
        using var client = new HttpClient(handler);
        await using var store = await OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => ranged
            ? store.GetRangeAsync("value", 0, 3).AsTask()
            : store.GetAsync("value").AsTask());

        Assert.True(handler.ContentDisposed);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("gcs-xml")]
    [InlineData("gcs-json")]
    public async Task ShouldAcceptMatchingOrAbsentDeclaredLengths(string provider)
    {
        foreach (var ranged in new[] { false, true })
        {
            foreach (var headers in new string[][] { [], ["3"], ["3", "3"] })
            {
                using var handler = new ResponseHandler(headers, ranged);
                using var client = new HttpClient(handler);
                await using var store = await OpenAsync(provider, client);

                var value = ranged
                    ? await store.GetRangeAsync("value", 0, 3)
                    : await store.GetAsync("value");

                Assert.Equal("abc"u8.ToArray(), Assert.IsType<CloudObject>(value).Data.ToArray());
                Assert.True(handler.ContentDisposed);
            }
        }
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("gcs-xml")]
    [InlineData("gcs-json")]
    public async Task ShouldBindReadIdentityToItsBodyWhenASameLengthReplacementFollowsTheRead(string provider)
    {
        using var handler = new ReplacingReadHandler();
        using var client = new HttpClient(handler);
        await using var store = await OpenAsync(provider, client);
        Assert.True(await store.PutAsync("value", "old"u8.ToArray(), new PantsCloudObjectWriteCondition.IfAbsent()));
        var original = Assert.IsType<CloudObjectMetadata>(await store.HeadAsync("value"));
        handler.AfterNextGet = async token =>
            Assert.True(await store.PutAsync("value", "new"u8.ToArray(), new PantsCloudObjectWriteCondition.Unconditional(), token));

        var proof = Assert.IsType<CloudObject>(await store.GetAsync("value"));

        Assert.Equal("old"u8.ToArray(), proof.Data.ToArray());
        Assert.Equal(original.Version, proof.Version);
        Assert.False(await store.PutAsync("value", "bad"u8.ToArray(), new PantsCloudObjectWriteCondition.IfVersion(proof.Version)));
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet,
            await store.DeleteAsync("value", new PantsCloudObjectDeleteCondition.IfVersion(proof.Version)));
        Assert.Equal("new"u8.ToArray(), (await store.GetAsync("value"))!.Data.ToArray());
    }

    [Fact]
    public async Task ShouldUseGenerationWhenGcsJsonMetadataAndMediaEtagsDiffer()
    {
        using var handler = new ReplacingReadHandler { DistinctMediaEtag = true };
        using var client = new HttpClient(handler);
        await using var store = await OpenAsync("gcs-json", client);
        Assert.True(await store.PutAsync("value", "old"u8.ToArray(), new PantsCloudObjectWriteCondition.IfAbsent()));

        var metadata = Assert.IsType<CloudObjectMetadata>(await store.HeadAsync("value"));
        var value = Assert.IsType<CloudObject>(await store.GetAsync("value"));
        var range = Assert.IsType<CloudObject>(await store.GetRangeAsync("value", 0, 3));

        Assert.NotEqual("\"media-etag\"", metadata.ETag);
        Assert.Equal(metadata.Generation, value.Version);
        Assert.Equal(value.Version, range.Version);
        Assert.True(await store.PutAsync("value", "new"u8.ToArray(), new PantsCloudObjectWriteCondition.IfVersion(value.Version)));
        Assert.Equal(CloudObjectDeleteOutcome.ConditionNotMet,
            await store.DeleteAsync("value", new PantsCloudObjectDeleteCondition.IfVersion(value.Version)));
        Assert.Equal("new"u8.ToArray(), (await store.GetAsync("value"))!.Data.ToArray());
    }

    static ValueTask<IPantsCloudObjectStore> OpenAsync(string provider, HttpClient client) =>
        CloudProviderTestFactory.OpenAsync(provider, client);

    sealed class ResponseHandler(string[] declaredLengths, bool ranged, bool knownLength = false) : HttpMessageHandler
    {
        public bool ContentDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new TestContent(() => ContentDisposed = true, knownLength)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"identity\"");
            response.Headers.TryAddWithoutValidation("x-goog-generation", "42");
            if (ranged)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 2, 3);
            }
            if (declaredLengths.Length != 0)
            {
                Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Length", declaredLengths));
            }

            return Task.FromResult(response);
        }
    }

    sealed class TestContent(Action disposed, bool knownLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("abc"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? 3 : 0;
            return knownLength;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed();
            }

            base.Dispose(disposing);
        }
    }

    sealed class ReplacingReadHandler() : DelegatingHandler(new InMemoryCloudProviderHandler())
    {
        public Func<CancellationToken, ValueTask>? AfterNextGet { get; set; }

        public bool DistinctMediaEtag { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal) && DistinctMediaEtag)
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"media-etag\"");
            }

            if (request.Method == HttpMethod.Get && AfterNextGet is { } replace)
            {
                AfterNextGet = null;
                await replace(cancellationToken);
            }

            return response;
        }
    }
}
