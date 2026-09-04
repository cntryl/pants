namespace Cntryl.Pants.Cloud;

public sealed record PantsAzureBlobProvider(
    string Account,
    string Container,
    Uri? Endpoint,
    PantsAzureCredentialSource Credential) : IPantsCloudProvider
{
    public PantsCloudProviderId Id => new("azure-blob");

    public PantsCloudValidationReport Validate() => BuiltInCloudProviderValidator.Validate(this);

    public ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
        PantsCloudProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPantsCloudObjectStore>(new AzureBlobObjectStore(
            this,
            context.Prefix,
            context.StorageHttpClient,
            context.OperationTimeout,
            context.CredentialHttpClient,
            context.TimeProvider));
    }
}
