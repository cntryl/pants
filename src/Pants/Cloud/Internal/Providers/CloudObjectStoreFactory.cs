namespace Pants;

internal static class CloudObjectStoreFactory
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 64
    })
    {
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
