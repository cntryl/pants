using System.Diagnostics;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudPreflightTests
{
    [Fact]
    public async Task ShouldDeduplicateSharedLocationAndUseOnlyReadOperations()
    {
        var store = new RecordingCloudObjectStore
        {
            Page = new CloudObjectListPage(["manifest/current"], null),
            Metadata = new CloudObjectMetadata(4, "version", null, null),
            Range = new CloudObject([42], "version")
        };
        var topology = PantsCloudStorageTopology.Shared(CreateLocation());

        var report = await CloudConfigurationPreflight.RunAsync(
            topology,
            new PantsCloudPreflightOptions(TimeSpan.FromSeconds(1)),
            (_, _) => store,
            TestContext.Current.CancellationToken);

        Assert.True(report.IsReady);
        Assert.True(report.IsFullyVerified);
        Assert.Equal(["List", "Head:manifest/current", "Range:manifest/current:0:1"], store.Calls);
        Assert.Equal(0, store.MutationCalls);
        Assert.All(
            report.Findings,
            static finding => Assert.Equal(
                [
                    PantsCloudStorageRole.Wal,
                    PantsCloudStorageRole.Sst,
                    PantsCloudStorageRole.Control
                ],
                finding.Roles));
    }

    [Fact]
    public async Task ShouldClassifyEmptyNamespaceWithoutIssuingAnObjectRead()
    {
        var store = new RecordingCloudObjectStore();

        var report = await CloudConfigurationPreflight.RunAsync(
            PantsCloudStorageTopology.Shared(CreateLocation()),
            PantsCloudPreflightOptions.Default,
            (_, _) => store,
            TestContext.Current.CancellationToken);

        Assert.True(report.IsReady);
        Assert.False(report.IsFullyVerified);
        Assert.Equal(["List"], store.Calls);
        Assert.Contains(
            report.Findings,
            static finding => finding.Code == PantsCloudCheckCode.ObjectHead &&
                              finding.FailureKind == PantsCloudFailureKind.NotApplicable);
        Assert.Equal(0, store.MutationCalls);
    }

    [Fact]
    public async Task ShouldApplyOneAbsoluteDeadlineAndClassifyTimeout()
    {
        var store = new RecordingCloudObjectStore { GateReads = true };
        var stopwatch = Stopwatch.StartNew();

        var report = await CloudConfigurationPreflight.RunAsync(
            PantsCloudStorageTopology.Shared(CreateLocation()),
            new PantsCloudPreflightOptions(TimeSpan.FromMilliseconds(30)),
            (_, _) => store,
            TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.False(report.IsReady);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains(
            report.Findings,
            static finding => finding.FailureKind == PantsCloudFailureKind.Timeout);
        Assert.Equal(0, store.MutationCalls);
    }

    [Theory]
    [InlineData(PantsCloudFailureKind.Authentication)]
    [InlineData(PantsCloudFailureKind.Authorization)]
    [InlineData(PantsCloudFailureKind.NotFound)]
    [InlineData(PantsCloudFailureKind.EndpointOrTls)]
    [InlineData(PantsCloudFailureKind.Provider)]
    public async Task ShouldReturnTypedProviderFailuresWithoutLeakingRawMessages(
        PantsCloudFailureKind failureKind)
    {
        const string secret = "PROVIDER-SECRET-RESPONSE";
        var store = new RecordingCloudObjectStore
        {
            Failure = new CloudPreflightException(failureKind, secret)
        };

        var report = await CloudConfigurationPreflight.RunAsync(
            PantsCloudStorageTopology.Shared(CreateLocation()),
            PantsCloudPreflightOptions.Default,
            (_, _) => store,
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(
            report.Findings,
            finding => finding.Code == PantsCloudCheckCode.NamespaceList);
        Assert.Equal(failureKind, failure.FailureKind);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.MutationCalls);
    }

    [Fact]
    public async Task ShouldPropagateCallerCancellationWithoutMutation()
    {
        var store = new RecordingCloudObjectStore { GateReads = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudConfigurationPreflight
            .RunAsync(
                PantsCloudStorageTopology.Shared(CreateLocation()),
                PantsCloudPreflightOptions.Default,
                (_, _) => store,
                cancellation.Token));

        Assert.Equal(0, store.MutationCalls);
    }

    [Fact]
    public async Task ShouldReportPartialTopologyFailureWithoutRepeatingPhysicalChecks()
    {
        var goodLocation = CreateLocation("good");
        var badLocation = CreateLocation("bad");
        var topology = new PantsCloudStorageTopology(goodLocation, goodLocation, badLocation);
        var stores = new Dictionary<string, RecordingCloudObjectStore>(StringComparer.Ordinal)
        {
            ["good"] = new(),
            ["bad"] = new()
            {
                Failure = new CloudPreflightException(
                    PantsCloudFailureKind.Authorization,
                    "raw-provider-body")
            }
        };

        var report = await CloudConfigurationPreflight.RunAsync(
            topology,
            PantsCloudPreflightOptions.Default,
            (location, _) => stores[location.Prefix],
            TestContext.Current.CancellationToken);

        Assert.False(report.IsReady);
        Assert.Equal(1, stores["good"].Calls.Count(static call => call == "List"));
        Assert.Equal(1, stores["bad"].Calls.Count(static call => call == "List"));
        Assert.Contains(
            report.Findings,
            static finding => finding.Roles.SequenceEqual([PantsCloudStorageRole.Control]) &&
                              finding.FailureKind == PantsCloudFailureKind.Authorization);
    }

    static PantsCloudStorageLocation CreateLocation(string prefix = "database") => new(
        new PantsCloudProviderConfiguration.S3Compatible(
            "bucket",
            "region",
            new Uri("https://objects.example.test"),
            true,
            new PantsS3CredentialSource.StaticCredentials("access", "secret")),
        prefix);

    sealed class RecordingCloudObjectStore : ICloudObjectStore
    {
        public List<string> Calls { get; } = [];

        public int MutationCalls { get; private set; }

        public CloudObjectListPage Page { get; init; } = new([], null);

        public CloudObjectMetadata? Metadata { get; init; }

        public CloudObject? Range { get; init; }

        public Exception? Failure { get; init; }

        public bool GateReads { get; init; }

        public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken)
        {
            Calls.Add($"Get:{objectKey}");
            return ValueTask.FromResult(Range);
        }

        public ValueTask<CloudObject?> GetRangeAsync(
            string objectKey,
            ulong offset,
            int length,
            CancellationToken cancellationToken)
        {
            Calls.Add($"Range:{objectKey}:{offset}:{length}");
            return ValueTask.FromResult(Range);
        }

        public ValueTask<CloudObjectMetadata?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken)
        {
            Calls.Add($"Head:{objectKey}");
            return ValueTask.FromResult(Metadata);
        }

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("Preflight must not write.");
        }

        public async ValueTask<CloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken)
        {
            Calls.Add("List");
            if (Failure is not null)
            {
                throw Failure;
            }

            if (GateReads)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Page;
        }

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
            string objectKey,
            CloudObjectDeleteCondition condition,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("Preflight must not delete.");
        }
    }
}
