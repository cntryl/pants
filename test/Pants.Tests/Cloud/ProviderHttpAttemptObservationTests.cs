using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderHttpAttemptObservationTests
{
    const string ActivityName = "Pants.Cloud.HttpAttempt";
    const string Prefix = "pants.cloud_io.";

    [Fact]
    public async Task ShouldRecordEveryTransientRetryWithoutObjectIdentifiers()
    {
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = ListenForAttempts(attempts);
        using var parent = new Activity("ProviderRetryTest").Start();
        var statuses = new Queue<HttpStatusCode>(
            [HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);
        using var handler = new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statuses.Dequeue())
            {
                Content = new ChunkedContent("value"u8.ToArray()),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"") }
            }));
        using var client = new HttpClient(handler);
        var store = new S3ObjectStore(
            new PantsAwsS3Provider(
                "bucket",
                "us-east-1",
                new PantsS3CredentialSource.StaticCredentials("access", "never-record-this-secret")),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        var value = await store.GetAsync("private-object-key", CancellationToken.None);

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<CloudObject>(value).Data));
        var observed = GetAttempts(attempts, parent);
        Assert.Equal(3, observed.Length);
        Assert.Equal(
            new[] { (int)HttpStatusCode.ServiceUnavailable, (int)HttpStatusCode.TooManyRequests, (int)HttpStatusCode.OK },
            observed.Select(activity => Tag<int>(activity, "status")));
        Assert.All(observed, activity =>
        {
            Assert.Equal(ActivityName, activity.DisplayName);
            Assert.Equal("GET", Tag<string>(activity, "method"));
            Assert.False(Tag<bool>(activity, "range"));
            Assert.Equal(0L, Tag<long>(activity, "request_body_bytes"));
            Assert.Equal(5L, Tag<long>(activity, "response_body_bytes"));
            Assert.False(Tag<bool>(activity, "transport_error"));
            Assert.False(Tag<bool>(activity, "cancelled"));
            Assert.True(Tag<long>(activity, "elapsed_ns") >= 0);
            Assert.Equal(ExpectedTagNames, activity.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(activity.TagObjects, tag =>
                tag.Value?.ToString()?.Contains("private-object-key", StringComparison.Ordinal) == true ||
                tag.Value?.ToString()?.Contains("never-record-this-secret", StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public async Task ShouldCountRequestAndChunkedResponseBodyBytes()
    {
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = ListenForAttempts(attempts);
        using var parent = new Activity("ProviderBodySizeTest").Start();
        using var handler = new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new ChunkedContent("reply"u8.ToArray())
            }));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://objects.example.test/private-key")
        {
            Content = new ReadOnlyMemoryContent("data"u8.ToArray())
        };

        using var response = await CloudHttpResponseReader.SendAsync(
            client,
            request,
            null,
            CancellationToken.None);

        var observed = Assert.Single(GetAttempts(attempts, parent));
        Assert.Equal("PUT", Tag<string>(observed, "method"));
        Assert.Equal(4L, Tag<long>(observed, "request_body_bytes"));
        Assert.Equal(5L, Tag<long>(observed, "response_body_bytes"));
        Assert.Equal((int)HttpStatusCode.Created, Tag<int>(observed, "status"));
    }

    [Fact]
    public async Task ShouldRecordTheRangeFlagAndBytesReadFromARangedResponse()
    {
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = ListenForAttempts(attempts);
        using var parent = new Activity("ProviderRangeTest").Start();
        using var handler = new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("range"u8.ToArray())
                {
                    Headers = { ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 4, 12) }
                }
            }));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://objects.example.test/private-key");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);

        using var response = await CloudHttpResponseReader.SendAsync(
            client,
            request,
            null,
            CancellationToken.None);

        var observed = Assert.Single(GetAttempts(attempts, parent));
        Assert.True(Tag<bool>(observed, "range"));
        Assert.Equal(5L, Tag<long>(observed, "response_body_bytes"));
        Assert.Equal((int)HttpStatusCode.PartialContent, Tag<int>(observed, "status"));
    }

    [Fact]
    public async Task ShouldPreserveResponseBufferLimitWhenObservationsAreEnabled()
    {
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = ListenForAttempts(attempts);
        using var parent = new Activity("ProviderBufferLimitTest").Start();
        using var handler = new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new ChunkedContent("reply"u8.ToArray())
            }));
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 4 };
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://objects.example.test/private-key");

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CloudHttpResponseReader.SendAsync(client, request, null, CancellationToken.None));

        var observed = Assert.Single(GetAttempts(attempts, parent));
        Assert.Equal((int)HttpStatusCode.Created, Tag<int>(observed, "status"));
        Assert.True(Tag<bool>(observed, "transport_error"));
        Assert.Equal(5L, Tag<long>(observed, "response_body_bytes"));
    }

    [Fact]
    public async Task ShouldDistinguishTransportFailureFromCallerCancellation()
    {
        var attempts = new ConcurrentQueue<Activity>();
        using var listener = ListenForAttempts(attempts);
        using var parent = new Activity("ProviderFailureTest").Start();
        using (var handler = new DelegateHandler((_, _) =>
                   Task.FromException<HttpResponseMessage>(new HttpRequestException("private-key and secret"))))
        using (var client = new HttpClient(handler))
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://objects.example.test/private-key"))
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await CloudHttpResponseReader.SendAsync(client, request, null, CancellationToken.None));
        }

        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var handler = new DelegateHandler(async (_, token) =>
               {
                   requestStarted.TrySetResult();
                   await Task.Delay(Timeout.Infinite, token);
                   return new HttpResponseMessage(HttpStatusCode.OK);
               }))
        using (var client = new HttpClient(handler))
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://objects.example.test/private-key"))
        {
            var send = CloudHttpResponseReader.SendAsync(
                client,
                request,
                null,
                cancellation.Token).AsTask();
            await requestStarted.Task.WaitAsync(TestTimeouts.Expected);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }

        var observed = GetAttempts(attempts, parent);
        Assert.Equal(2, observed.Length);
        var transportFailure = Assert.Single(observed, activity => Tag<bool>(activity, "transport_error"));
        Assert.False(Tag<bool>(transportFailure, "cancelled"));
        Assert.Equal(0, Tag<int>(transportFailure, "status"));
        var cancelled = Assert.Single(observed, activity => Tag<bool>(activity, "cancelled"));
        Assert.False(Tag<bool>(cancelled, "transport_error"));
        Assert.Equal(0, Tag<int>(cancelled, "status"));
        Assert.All(observed, activity => Assert.Equal(ExpectedTagNames, activity.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal)));
    }

    static ActivityListener ListenForAttempts(ConcurrentQueue<Activity> attempts)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Pants",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => attempts.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static Activity[] GetAttempts(ConcurrentQueue<Activity> attempts, Activity parent) =>
        attempts.Where(activity => activity.ParentId == parent.Id).ToArray();

    static T Tag<T>(Activity activity, string name) =>
        Assert.IsType<T>(activity.GetTagItem(Prefix + name));

    static string[] ExpectedTagNames => new[]
    {
        Prefix + "method",
        Prefix + "range",
        Prefix + "request_body_bytes",
        Prefix + "response_body_bytes",
        Prefix + "status",
        Prefix + "transport_error",
        Prefix + "cancelled",
        Prefix + "elapsed_ns"
    }.Order(StringComparer.Ordinal).ToArray();

    sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }

    sealed class ChunkedContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => stream.WriteAsync(content, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
