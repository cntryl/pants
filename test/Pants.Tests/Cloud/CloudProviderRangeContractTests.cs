using System.Net;
using System.Net.Http.Headers;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudProviderRangeContractTests
{
    public static TheoryData<string> Providers => new("s3", "azure", "gcs-xml", "gcs-json");

    public static TheoryData<string, HttpStatusCode, string[]> InvalidRanges
    {
        get
        {
            var data = new TheoryData<string, HttpStatusCode, string[]>();
            foreach (var provider in new[] { "s3", "azure", "gcs-xml", "gcs-json" })
            {
                data.Add(provider, HttpStatusCode.OK, ["bytes 2-4/10"]);
                data.Add(provider, HttpStatusCode.NoContent, []);
                foreach (var headers in new string[][]
                         {
                             [], ["invalid"], ["bytes 0-2/10"], ["bytes 2-5/10"],
                             ["items 2-4/10"], ["bytes */10"], ["bytes 2-4/4"],
                             ["bytes 2-4/10", "bytes 2-4/10"]
                         })
                {
                    data.Add(provider, HttpStatusCode.PartialContent, headers);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public async Task ShouldRejectIgnoredOrIncorrectRangesBeforeReadingTheBody(
        string provider,
        HttpStatusCode status,
        string[] contentRanges)
    {
        using var body = new ObservedStream(3);
        using var handler = new RangeHandler(() => body, status, contentRanges);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());

        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldRejectAnOversizedDeclarationWithoutReadingTheBody(string provider)
    {
        using var body = new ObservedStream(64 * 1024);
        using var handler = new RangeHandler(() => body) { DeclaredLength = 64 * 1024 };
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());

        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldStopAfterOneExcessByteGivenAnUndeclaredOversizedRangeBody(string provider)
    {
        using var body = new ObservedStream(64 * 1024);
        using var handler = new RangeHandler(() => body);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());

        Assert.Equal(4, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldRejectTruncatedRangeBodiesAndDisposeThem(string provider)
    {
        using var body = new ObservedStream(2);
        using var handler = new RangeHandler(() => body);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());

        Assert.Equal(2, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldReadExactRangesAcrossShortReadsWithOptionalLengthHeaders(string provider)
    {
        foreach (var total in new[] { "10", "*" })
        {
            foreach (var declaredLength in new long?[] { null, 3 })
            {
                using var body = new ObservedStream(3) { MaximumRead = 1 };
                using var handler = new RangeHandler(() => body, contentRanges: [$"bytes 2-4/{total}"])
                {
                    DeclaredLength = declaredLength
                };
                using var client = new HttpClient(handler);
                await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

                var value = Assert.IsType<PantsCloudObject>(await store.GetRangeAsync("value", 2, 3));

                Assert.Equal("cde"u8.ToArray(), value.Data.ToArray());
                Assert.Equal(provider.StartsWith("gcs", StringComparison.Ordinal) ? "42" : "\"identity\"", value.Version);
                Assert.Equal(3, body.BytesRead);
                Assert.True(body.Disposed);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldRespectTheHttpClientBufferLimitBeforeReadingARange(string provider)
    {
        var bodies = new List<ObservedStream>();
        using var handler = new RangeHandler(() =>
        {
            var body = new ObservedStream(3);
            bodies.Add(body);
            return body;
        });
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 2 };
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());

        Assert.NotEmpty(bodies);
        Assert.All(bodies, body =>
        {
            Assert.Equal(0, body.BytesRead);
            Assert.True(body.Disposed);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldDiscardRangeErrorBodiesWithoutChangingStatusOrRetryBehavior(string provider)
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            var bodies = new List<ObservedStream>();
            using var handler = new RangeHandler(() =>
            {
                var body = new ObservedStream(64 * 1024);
                bodies.Add(body);
                return body;
            }, status, []);
            using var client = new HttpClient(handler);
            await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

            if (status == HttpStatusCode.NotFound)
            {
                Assert.Null(await store.GetRangeAsync("value", 2, 3));
            }
            else
            {
                await Assert.ThrowsAsync<PantsIOException>(() => store.GetRangeAsync("value", 2, 3).AsTask());
            }

            Assert.Equal(status == HttpStatusCode.ServiceUnavailable ? 3 : 1, handler.Requests);
            Assert.All(bodies, body =>
            {
                Assert.Equal(0, body.BytesRead);
                Assert.True(body.Disposed);
            });
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldKeepCancellationActiveWhileConfirmingRangeEndOfStream(string provider)
    {
        using var body = new ObservedStream(3) { StallAtEnd = true };
        using var handler = new RangeHandler(() => body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client);

        var read = store.GetRangeAsync("value", 2, 3, cancellation.Token).AsTask();
        await body.EndReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(body.Disposed);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ShouldKeepTheOperationDeadlineActiveWhileConfirmingRangeEndOfStream(string provider)
    {
        using var body = new ObservedStream(3) { StallAtEnd = true };
        using var handler = new RangeHandler(() => body);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(provider, client, TimeSpan.FromMilliseconds(100));

        var read = store.GetRangeAsync("value", 2, 3).AsTask();

        await Assert.ThrowsAsync<PantsTimeoutException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body.EndReadStarted.Task.IsCompletedSuccessfully);
        Assert.True(body.Disposed);
    }

    sealed class RangeHandler(
        Func<ObservedStream> createBody,
        HttpStatusCode status = HttpStatusCode.PartialContent,
        string[]? contentRanges = null) : HttpMessageHandler
    {
        public long? DeclaredLength { get; init; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("bytes=2-4", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(status) { Content = new StreamContent(createBody()) };
            response.Headers.ETag = new EntityTagHeaderValue("\"identity\"");
            response.Headers.TryAddWithoutValidation("x-goog-generation", "42");
            response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRanges ?? ["bytes 2-4/10"]);
            response.Content.Headers.ContentLength = DeclaredLength;
            return Task.FromResult(response);
        }
    }

    sealed class ObservedStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public int MaximumRead { get; init; } = int.MaxValue;
        public bool StallAtEnd { get; init; }
        public TaskCompletionSource EndReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead == length && StallAtEnd)
            {
                EndReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var count = Math.Min(Math.Min(buffer.Length, MaximumRead), length - BytesRead);
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = (byte)('c' + (BytesRead + index) % 3);
            }

            BytesRead += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
