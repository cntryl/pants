using System.Net;

namespace Cntryl.Pants.Cloud.Internal.Providers;

static class CloudObjectStoreFactory
{
    internal static readonly TimeSpan CredentialConnectTimeout = TimeSpan.FromSeconds(1);

    internal static HttpClient StorageHttpClient { get; } = new(CreateStorageHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static HttpClient CredentialHttpClient { get; } = new(CreateCredentialHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static SocketsHttpHandler CreateStorageHandler() => CreateHandler(null);

    internal static SocketsHttpHandler CreateCredentialHandler() =>
        CreateHandler(CredentialConnectTimeout);

    static SocketsHttpHandler CreateHandler(TimeSpan? connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 64,
            UseCookies = false
        };
        if (connectTimeout is { } timeout)
        {
            handler.ConnectTimeout = timeout;
        }

        return handler;
    }

    public static ValueTask<ICloudObjectStore> CreateAsync(
        PantsCloudStorageLocation location,
        TimeSpan timeout,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        var storageClient = httpClient ?? StorageHttpClient;
        var credentialClient = httpClient ?? CredentialHttpClient;
        return OpenAsync();

        async ValueTask<ICloudObjectStore> OpenAsync()
        {
            return (ICloudObjectStore)await location.Provider.OpenObjectStoreAsync(
                new PantsCloudProviderContext(
                    location.Prefix,
                    timeout,
                    storageClient,
                    credentialClient,
                    timeProvider ?? TimeProvider.System),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
