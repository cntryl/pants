using System.Net;
using System.Runtime.InteropServices;
using Cntryl.Pants.Cloud.Internal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderObjectStorePayloadTests
{
    [Theory]
    [InlineData("s3", false)]
    [InlineData("s3", true)]
    [InlineData("gcs", false)]
    [InlineData("azure", false)]
    public async Task ShouldWritePutPayloadFromCallerMemoryWithoutPerAttemptCopy(
        string provider,
        bool retryConditionalConflict)
    {
        var payload = Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray();
        var handler = new PayloadObservingHandler(payload, retryConditionalConflict);
        using var client = new HttpClient(handler);
        await using var store = CreateStore(provider, client);

        Assert.True(await store.PutAsync(
            "object",
            payload,
            new PantsCloudObjectWriteCondition.IfAbsent(),
            CancellationToken.None));

        Assert.Equal(retryConditionalConflict ? 2 : 1, handler.Attempts.Count);
        Assert.All(handler.Attempts, attempt =>
        {
            Assert.True(attempt.UsedCallerBuffer);
            Assert.Equal(payload, attempt.Body);
        });
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    [InlineData("azure")]
    public async Task ShouldRetainUploadReservationUntilCanceledTransportCompletes(string provider)
    {
        using var cache = new TemporaryDirectory();
        var leaseStore = new TestCloudLeaseStore();
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            new ManualClock(DateTimeOffset.UnixEpoch),
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var payload = new byte[4096];
        var budget = new ResourceBudget(2 * ImmutablePublicationEnvelope.For(payload.Length));
        var handler = new StalledUploadHandler();
        using var client = new HttpClient(handler);
        await using var persistence = new ProviderCloudPersistence(
            cache.Path,
            CreateStore(provider, client),
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            lease,
            walPublicationAdmission: new WalPublicationAdmission(budget));
        using var cancellation = new CancellationTokenSource();
        var upload = persistence.PublishWalBatchAsync(
            [new SealedWalSegment(1, epoch, 1, "1.wal", payload)],
            cancellation.Token).AsTask();

        await handler.UploadStarted.Task.WaitAsync(TestTimeouts.Expected);
        cancellation.Cancel();
        await handler.CancellationObserved.Task.WaitAsync(TestTimeouts.Expected);

        Assert.Equal(ImmutablePublicationEnvelope.For(payload.Length), budget.Current);

        handler.CompleteTransport.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Equal(0, budget.Current);
    }

    static IPantsCloudObjectStore CreateStore(string provider, HttpClient client) => provider switch
    {
        "s3" => new S3ObjectStore(
            new PantsS3CompatibleProvider(
                "bucket",
                "us-test-1",
                new Uri("https://objects.example.test/base"),
                true,
                new PantsS3CredentialSource.StaticCredentials("access", "secret")),
            "database",
            client,
            TimeSpan.FromSeconds(5)),
        "gcs" => new GcsObjectStore(
            new PantsGcsProvider(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Xml,
                new PantsGcsCredentialSource.HmacKey("access", "secret")),
            "database",
            client,
            TimeSpan.FromSeconds(5)),
        "azure" => new AzureBlobObjectStore(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://azure.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=value")),
            "database",
            client,
            TimeSpan.FromSeconds(5)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    sealed class PayloadObservingHandler(byte[] payload, bool retryConditionalConflict)
        : HttpMessageHandler
    {
        readonly byte[] _payload = payload;
        int _requestCount;

        public List<PayloadAttempt> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var stream = new PayloadObservingStream(_payload);
            await request.Content!.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            Attempts.Add(new PayloadAttempt(stream.UsedCallerBuffer, stream.ToArray()));

            if (retryConditionalConflict && Interlocked.Increment(ref _requestCount) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        "<Error><Code>ConditionalRequestConflict</Code></Error>")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class PayloadObservingStream(byte[] payload) : MemoryStream
    {
        public bool UsedCallerBuffer { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            UsedCallerBuffer |= ReferenceEquals(buffer, payload);
            base.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            UsedCallerBuffer |= ReferenceEquals(buffer, payload);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            UsedCallerBuffer |= MemoryMarshal.TryGetArray(buffer, out var segment) &&
                                ReferenceEquals(segment.Array, payload);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    sealed class StalledUploadHandler : HttpMessageHandler
    {
        public TaskCompletionSource UploadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompleteTransport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            UploadStarted.TrySetResult();
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                CancellationObserved);
            await CompleteTransport.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed record PayloadAttempt(bool UsedCallerBuffer, byte[] Body);
}
