namespace Cntryl.Pants.Cloud;

public sealed record PantsOciObjectStorageProvider(
    string Namespace,
    string Bucket,
    string Region,
    Uri? Endpoint,
    PantsOciCredentialSource Credentials) : IPantsCloudProvider
{
    public Uri EffectiveEndpoint => Endpoint ?? new Uri(
        $"https://{Namespace}.compat.objectstorage.{Region}.oraclecloud.com");

    public PantsCloudProviderId Id => new("oci-object-storage");

    public PantsCloudValidationReport Validate() => BuiltInCloudProviderValidator.Validate(this);

    public ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
        PantsCloudProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPantsCloudObjectStore>(new S3ObjectStore(
            this,
            context.Prefix,
            context.StorageHttpClient,
            context.OperationTimeout,
            context.CredentialHttpClient,
            context.TimeProvider));
    }
}
