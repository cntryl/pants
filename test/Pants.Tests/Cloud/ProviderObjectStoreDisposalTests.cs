using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderObjectStoreDisposalTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ShouldAttemptEveryDistinctStoreOnceBeforePropagatingDisposalFailures(
        bool persistenceOwnsStores,
        bool sharedStore,
        bool multipleFailures)
    {
        using var directory = new TemporaryDirectory();
        using var lease = new CloudLeaseCoordinator(
            new TestCloudLeaseStore(),
            SystemPantsClock.Instance,
            "test-holder",
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero);
        var walFailure = new InvalidOperationException("WAL cleanup failed.");
        var sstFailure = new IOException("SST cleanup failed.");
        var wal = new DisposalTrackingCloudObjectStore(walFailure);
        var sst = sharedStore ? wal : new DisposalTrackingCloudObjectStore(multipleFailures ? sstFailure : null);
        var control = sharedStore ? wal : new DisposalTrackingCloudObjectStore();
        IAsyncDisposable owner = persistenceOwnsStores
            ? new ProviderCloudPersistence(directory.Path, wal, sst, control, lease)
            : new ProviderObjectStoreSet(wal, sst, control);

        var exception = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        await owner.DisposeAsync();

        Assert.Equal(1, wal.DisposeCount);
        Assert.Equal(1, sst.DisposeCount);
        Assert.Equal(1, control.DisposeCount);
        if (multipleFailures)
        {
            var aggregate = Assert.IsType<AggregateException>(exception);
            Assert.Equal([walFailure, sstFailure], aggregate.InnerExceptions);
        }
        else
        {
            Assert.Same(walFailure, exception);
        }
    }

    [Fact]
    public async Task ShouldPreserveOpenFailureAfterAttemptingEveryOpenedStoreCleanup()
    {
        var openFailure = new InvalidOperationException("Provider initialization failed.");
        var wal = new DisposalTrackingCloudObjectStore(new IOException("WAL cleanup failed."));
        var sst = new DisposalTrackingCloudObjectStore(new IOException("SST cleanup failed."));
        var topology = new PantsCloudStorageTopology(
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<ICloudObjectStore>(wal)), "wal"),
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<ICloudObjectStore>(sst)), "sst"),
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromException<ICloudObjectStore>(openFailure)), "control"));

        var exception = await Record.ExceptionAsync(() => ProviderObjectStoreSet.OpenAsync(
            topology,
            TimeSpan.FromSeconds(30),
            null,
            TimeProvider.System,
            CancellationToken.None).AsTask());

        Assert.Equal(1, wal.DisposeCount);
        Assert.Equal(1, sst.DisposeCount);
        Assert.Same(openFailure, exception);
    }
}
