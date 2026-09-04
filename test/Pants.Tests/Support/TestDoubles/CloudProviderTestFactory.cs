namespace Cntryl.Pants.Support.TestDoubles;

static class CloudProviderTestFactory
{
    public static ValueTask<IPantsCloudObjectStore> OpenAsync(
        string provider,
        HttpClient client,
        TimeSpan? operationTimeout = null)
    {
        var endpoint = new Uri("https://storage.example.test");
        IPantsCloudProvider configuration = provider switch
        {
            "s3" => new PantsS3CompatibleProvider("bucket", "region", endpoint, true,
                new PantsS3CredentialSource.StaticCredentials("test", "test")),
            "azure" => new PantsAzureBlobProvider("account", "container", endpoint,
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "gcs-xml" => new PantsGcsProvider("bucket", "project", endpoint, PantsGcsApiStyle.Xml,
                new PantsGcsCredentialSource.HmacKey("test", "test")),
            "gcs-json" => new PantsGcsProvider("bucket", "project", endpoint, PantsGcsApiStyle.Json,
                new PantsGcsCredentialSource.BearerToken("test")),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        return configuration.OpenObjectStoreAsync(new PantsCloudProviderContext(
            "", operationTimeout ?? TimeSpan.FromSeconds(5), client, client, TimeProvider.System));
    }
}
