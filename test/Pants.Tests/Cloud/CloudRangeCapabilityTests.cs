namespace Cntryl.Pants.Cloud;

public sealed class CloudRangeCapabilityTests
{
    [Fact]
    public async Task ShouldRejectUnsupportedRangesWithoutFallingBackToAFullObjectRead()
    {
        var implementation = new FullReadOnlyStore();
        IPantsCloudObjectStore store = implementation;

        await Assert.ThrowsAsync<PantsNotSupportedException>(() => store.GetRangeAsync("value", 0, 1).AsTask());

        Assert.Equal(0, implementation.FullReads);
    }

    [Fact]
    public async Task ShouldHonorCancellationBeforeReportingUnsupportedRanges()
    {
        var implementation = new FullReadOnlyStore();
        IPantsCloudObjectStore store = implementation;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetRangeAsync("value", 0, 1, cancellation.Token).AsTask());

        Assert.Equal(0, implementation.FullReads);
    }

    [Fact]
    public async Task ShouldFailPreflightForUnsupportedRangesWithoutDownloadingAnObject()
    {
        var store = new FullReadOnlyStore();
        var location = new PantsCloudStorageLocation(new FullReadOnlyProvider(store), "test");

        var report = await location.PreflightAsync();

        Assert.False(report.IsReady);
        var finding = Assert.Single(report.Findings, finding => finding.Code == PantsCloudCheckCode.RangedRead);
        Assert.Equal(PantsCloudCheckOutcome.Failed, finding.Outcome);
        Assert.Equal(PantsCloudFailureKind.Unsupported, finding.FailureKind);
        Assert.Equal(0, store.FullReads);
    }

    [Fact]
    public async Task ShouldNotDownloadAnSstThroughAnUnsupportedRangeFallback()
    {
        var store = new FullReadOnlyStore();
        var factory = new ProviderCloudSstSourceFactory(store);
        await using var source = await factory.OpenAsync(new FileMeta { Name = "value.sst", SizeBytes = 4 }, CancellationToken.None);
        Assert.NotNull(source);

        await Assert.ThrowsAsync<PantsNotSupportedException>(() => source.ReadExactlyAsync(0, 1, CancellationToken.None).AsTask());

        Assert.Equal(0, store.FullReads);
    }

    sealed class FullReadOnlyProvider(FullReadOnlyStore store) : IPantsCloudProvider
    {
        public PantsCloudProviderId Id => new("full-read-only");
        public PantsCloudValidationReport Validate() => new([]);
        public ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
            PantsCloudProviderContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IPantsCloudObjectStore>(store);
    }

    sealed class FullReadOnlyStore : IPantsCloudObjectStore
    {
        public int FullReads { get; private set; }

        public ValueTask<PantsCloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullReads++;
            return ValueTask.FromResult<PantsCloudObject?>(new("data"u8.ToArray(), "version"));
        }

        public ValueTask<PantsCloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PantsCloudObjectMetadata?>(new(4, "version", null, null));

        public ValueTask<PantsCloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new PantsCloudObjectListPage(["value"], null));

        public ValueTask<bool> PutAsync(string objectKey, ReadOnlyMemory<byte> data, PantsCloudObjectWriteCondition condition,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<PantsCloudObjectDeleteOutcome> DeleteAsync(string objectKey, PantsCloudObjectDeleteCondition condition,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
