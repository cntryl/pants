using System.Net;

namespace Cntryl.Pants.Cloud.Internal.Providers;

static class CloudObjectStoreFactory
{
    static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        EnableMultipleHttp2Connections = true,
        MaxResponseHeadersLength = 64,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 64,
        UseCookies = false
    })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static ICloudObjectStore Create(
        PantsCloudStorageLocation location,
        TimeSpan timeout,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Provider switch
        {
            PantsCloudProviderConfiguration.AzureBlob azure => new AzureBlobObjectStore(
                azure,
                location.Prefix,
                httpClient ?? SharedHttpClient,
                timeout),
            PantsCloudProviderConfiguration.AwsS3 or
                PantsCloudProviderConfiguration.S3Compatible => new S3ObjectStore(
                    location.Provider,
                    location.Prefix,
                    httpClient ?? SharedHttpClient,
                    timeout),
            PantsCloudProviderConfiguration.Gcs gcs => new GcsObjectStore(
                gcs,
                location.Prefix,
                httpClient ?? SharedHttpClient,
                timeout),
            _ => throw new PantsNotSupportedException("The cloud provider is unsupported.")
        };
    }
}
