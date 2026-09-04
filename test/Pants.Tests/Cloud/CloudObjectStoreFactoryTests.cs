using System.Net;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudObjectStoreFactoryTests
{
    [Fact]
    public void ShouldUseEndpointCompatibleStorageHttpDefaults()
    {
        using var handler = CloudObjectStoreFactory.CreateStorageHandler();

        Assert.Equal(Timeout.InfiniteTimeSpan, handler.ConnectTimeout);
        Assert.False(handler.UseCookies);
        Assert.Equal(64, handler.MaxConnectionsPerServer);
        Assert.Equal(HttpVersion.Version11, CloudObjectStoreFactory.StorageHttpClient.DefaultRequestVersion);
        Assert.Equal(
            HttpVersionPolicy.RequestVersionOrLower,
            CloudObjectStoreFactory.StorageHttpClient.DefaultVersionPolicy);
        Assert.Equal(Timeout.InfiniteTimeSpan, CloudObjectStoreFactory.StorageHttpClient.Timeout);
    }

    [Fact]
    public void ShouldUseShortIndependentCredentialConnectTimeout()
    {
        using var storageHandler = CloudObjectStoreFactory.CreateStorageHandler();
        using var credentialHandler = CloudObjectStoreFactory.CreateCredentialHandler();

        Assert.Equal(Timeout.InfiniteTimeSpan, storageHandler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), credentialHandler.ConnectTimeout);
        Assert.NotSame(
            CloudObjectStoreFactory.StorageHttpClient,
            CloudObjectStoreFactory.CredentialHttpClient);
    }

    [Fact]
    public async Task ShouldOpenThirdPartyProviderWithoutCoreRegistration()
    {
        var provider = new RecordingProvider();
        var location = new PantsCloudStorageLocation(provider, "tenant/catalog");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        await using var store = await CloudObjectStoreFactory.CreateAsync(
            location,
            TimeSpan.FromSeconds(7),
            timeProvider: timeProvider);

        Assert.Same(provider.Store, store);
        Assert.Equal(new PantsCloudProviderId("example-object-store"), provider.Id);
        Assert.Equal("tenant/catalog", Assert.IsType<PantsCloudProviderContext>(provider.Context).Prefix);
        Assert.Equal(TimeSpan.FromSeconds(7), provider.Context.OperationTimeout);
        Assert.Same(timeProvider, provider.Context.TimeProvider);
        Assert.True(location.Validate().IsValid);
    }

    [Fact]
    public async Task ShouldDisposeSharedThirdPartyStoreExactlyOnce()
    {
        var provider = new RecordingProvider();
        var location = new PantsCloudStorageLocation(provider, "tenant/catalog");
        var stores = await ProviderObjectStoreSet.OpenAsync(
            PantsCloudStorageTopology.Shared(location),
            TimeSpan.FromSeconds(7),
            null,
            TimeProvider.System,
            CancellationToken.None);

        await stores.DisposeAsync();
        await stores.DisposeAsync();

        Assert.Equal(3, provider.OpenCount);
        Assert.Equal(1, provider.Store.DisposeCount);
    }

    sealed class RecordingProvider : IPantsCloudProvider
    {
        public DisposalTrackingStore Store { get; } = new();

        public int OpenCount { get; private set; }

        public PantsCloudProviderContext? Context { get; private set; }
        public PantsCloudProviderId Id => new("example-object-store");

        public PantsCloudValidationReport Validate() => new([]);

        public ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
            PantsCloudProviderContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            OpenCount++;
            return ValueTask.FromResult<IPantsCloudObjectStore>(Store);
        }
    }

    sealed class DisposalTrackingStore : IPantsCloudObjectStore
    {
        readonly TestCloudObjectStore _inner = new();

        public int DisposeCount { get; private set; }

        public ValueTask<PantsCloudObject?> GetAsync(
            string objectKey,
            CancellationToken cancellationToken = default) =>
            _inner.GetAsync(objectKey, cancellationToken);

        public ValueTask<PantsCloudObjectMetadata?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken = default) =>
            _inner.HeadAsync(objectKey, cancellationToken);

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            PantsCloudObjectWriteCondition condition,
            CancellationToken cancellationToken = default) =>
            _inner.PutAsync(objectKey, data, condition, cancellationToken);

        public ValueTask<PantsCloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken = default) =>
            _inner.ListPageAsync(prefix, continuationToken, cancellationToken);

        public ValueTask<PantsCloudObjectDeleteOutcome> DeleteAsync(
            string objectKey,
            PantsCloudObjectDeleteCondition condition,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(objectKey, condition, cancellationToken);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
