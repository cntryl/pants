namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.S3;

sealed class StaticS3CredentialProvider(S3Credentials credentials) : IS3CredentialProvider
{
    readonly S3Credentials _credentials = credentials;

    public ValueTask<S3Credentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_credentials);
    }
}
