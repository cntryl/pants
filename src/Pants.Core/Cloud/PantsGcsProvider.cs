namespace Cntryl.Pants.Cloud;

public sealed record PantsGcsProvider(
    string Bucket,
    string ProjectId,
    Uri? Endpoint,
    PantsGcsApiStyle ApiStyle,
    PantsGcsCredentialSource Credential) : IPantsCloudProvider
{
    public PantsCloudProviderId Id => new("gcs");

    public PantsCloudValidationReport Validate() => BuiltInCloudProviderValidator.Validate(this);

    public ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
        PantsCloudProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPantsCloudObjectStore>(new GcsObjectStore(
            this,
            context.Prefix,
            context.StorageHttpClient,
            context.OperationTimeout,
            context.CredentialHttpClient,
            context.TimeProvider));
    }
}
