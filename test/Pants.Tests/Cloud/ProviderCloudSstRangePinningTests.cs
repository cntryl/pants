using System.Net;
using System.Net.Http.Headers;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudSstRangePinningTests
{
    public static TheoryData<string> Providers => new("s3", "azure", "gcs-xml", "gcs-json");

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldClassifyReplacementDuringSstRangeAsConcurrentChange(string provider)
    {
        using var handler = new PinnedRangeHandler(provider, replaceAfterHead: true);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);
        var factory = new ProviderCloudSstSourceFactory(store);
        await using var source = await factory.OpenAsync(
            new FileMeta { Name = "read.sst", SizeBytes = 4 },
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PantsBusyException>(() =>
            source!.ReadExactlyAsync(0, 2, CancellationToken.None).AsTask());

        Assert.Equal(PantsErrorCode.Busy, failure.Code);
        Assert.True(handler.PinnedConditionObserved);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(1, handler.RangeRequests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldRequireSstRangeTotalToMatchHeadSize(string provider)
    {
        using var body = new ObservedStream(2);
        using var handler = new PinnedRangeHandler(provider, body, rangeTotalLength: 5);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);
        var factory = new ProviderCloudSstSourceFactory(store);
        await using var source = await factory.OpenAsync(
            new FileMeta { Name = "read.sst", SizeBytes = 4 },
            CancellationToken.None);

        await Assert.ThrowsAsync<PantsIOException>(() =>
            source!.ReadExactlyAsync(0, 2, CancellationToken.None).AsTask());

        Assert.True(body.Disposed);
        Assert.Equal(0, body.BytesRead);
        Assert.True(handler.PinnedConditionObserved);
    }

    sealed class PinnedRangeHandler : HttpMessageHandler
    {
        readonly ObservedStream? _rangeBody;
        readonly string _provider;
        readonly bool _replaceAfterHead;
        readonly long _rangeTotalLength;

        public PinnedRangeHandler(
            string provider,
            bool replaceAfterHead = false,
            long rangeTotalLength = 4) : this(
            provider,
            null,
            replaceAfterHead,
            rangeTotalLength)
        {
        }

        public PinnedRangeHandler(
            string provider,
            ObservedStream? rangeBody,
            long rangeTotalLength) : this(
            provider,
            rangeBody,
            false,
            rangeTotalLength)
        {
        }

        PinnedRangeHandler(
            string provider,
            ObservedStream? rangeBody,
            bool replaceAfterHead,
            long rangeTotalLength)
        {
            _provider = provider;
            _rangeBody = rangeBody;
            _replaceAfterHead = replaceAfterHead;
            _rangeTotalLength = rangeTotalLength;
        }

        public bool PinnedConditionObserved { get; private set; }

        public int MetadataRequests { get; private set; }

        public int RangeRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (IsMetadataRequest(request))
            {
                MetadataRequests++;
                return Task.FromResult(CreateMetadataResponse());
            }

            if (request.Headers.Range is null)
            {
                throw new InvalidOperationException("Unexpected provider request.");
            }

            RangeRequests++;
            PinnedConditionObserved = HasExpectedCondition(request);
            if (_replaceAfterHead && PinnedConditionObserved)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                {
                    Content = new ByteArrayContent([])
                });
            }

            HttpContent rangeBody = _rangeBody is null
                ? new ByteArrayContent("ab"u8.ToArray())
                : new StreamContent(_rangeBody);
            rangeBody.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, _rangeTotalLength);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = rangeBody
            };
            response.Headers.ETag = new EntityTagHeaderValue(
                _replaceAfterHead ? "\"v2\"" : "\"v1\"");
            response.Headers.TryAddWithoutValidation(
                "x-goog-generation",
                _replaceAfterHead ? "8" : "7");
            return Task.FromResult(response);
        }

        bool IsMetadataRequest(HttpRequestMessage request) => _provider switch
        {
            "s3" or "azure" or "gcs-xml" => request.Method == HttpMethod.Head,
            "gcs-json" => request.Method == HttpMethod.Get &&
                          !request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unknown provider '{_provider}'.")
        };

        bool HasExpectedCondition(HttpRequestMessage request) => _provider switch
        {
            "s3" or "azure" => request.Headers.IfMatch.SingleOrDefault()?.Tag == "\"v1\"",
            "gcs-xml" => request.Headers.TryGetValues(
                             "x-goog-if-generation-match",
                             out var values) && values.SingleOrDefault() == "7",
            "gcs-json" => request.RequestUri!.Query.Contains(
                "ifGenerationMatch=7",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unknown provider '{_provider}'.")
        };

        HttpResponseMessage CreateMetadataResponse()
        {
            if (_provider == "gcs-json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"size\":\"4\",\"etag\":\"etag-v1\",\"generation\":\"7\"}")
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
                {
                    Headers = { ContentLength = 4 }
                },
                Headers = { ETag = new EntityTagHeaderValue("\"v1\"") }
            };
            if (_provider == "gcs-xml")
            {
                response.Headers.TryAddWithoutValidation("x-goog-generation", "7");
            }

            return response;
        }
    }

    sealed class ObservedStream(int length) : Stream
    {
        public int BytesRead { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Fill((byte)'a');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
