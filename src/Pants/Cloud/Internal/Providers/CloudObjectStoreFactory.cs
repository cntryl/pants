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

    public static ICloudObjectStore Create(
        PantsCloudStorageLocation location,
        TimeSpan timeout,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        var storageClient = httpClient ?? StorageHttpClient;
        var credentialClient = httpClient ?? CredentialHttpClient;
        return location.Provider switch
        {
            PantsCloudProviderConfiguration.AzureBlob azure => new AzureBlobObjectStore(
                azure,
                location.Prefix,
                storageClient,
                timeout,
                credentialClient),
            PantsCloudProviderConfiguration.AwsS3 or
                PantsCloudProviderConfiguration.S3Compatible => new S3ObjectStore(
                    location.Provider,
                    location.Prefix,
                    storageClient,
                    timeout,
                    credentialClient),
            PantsCloudProviderConfiguration.Gcs gcs => new GcsObjectStore(
                gcs,
                location.Prefix,
                storageClient,
                timeout,
                credentialClient),
            _ => throw new PantsNotSupportedException("The cloud provider is unsupported.")
        };
    }
}
