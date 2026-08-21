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
            PantsCloudProviderConfiguration.AwsS3 => throw ProviderNotQualified("AWS S3"),
            PantsCloudProviderConfiguration.S3Compatible => throw ProviderNotQualified(
                "S3-compatible/OCI"),
            PantsCloudProviderConfiguration.Gcs => throw ProviderNotQualified("GCS"),
            _ => throw new PantsNotSupportedException("The cloud provider is unsupported.")
        };
    }

    private static PantsNotSupportedException ProviderNotQualified(string provider) => new(
        $"The direct-HTTP {provider} client has not completed qualification.");
}
