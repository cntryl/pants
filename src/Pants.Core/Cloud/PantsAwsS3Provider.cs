namespace Cntryl.Pants.Cloud;

public sealed record PantsAwsS3Provider(
    string Bucket,
    string Region,
    PantsS3CredentialSource Credentials) : IPantsCloudProvider
{
    public PantsCloudProviderId Id => new("aws-s3");

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
