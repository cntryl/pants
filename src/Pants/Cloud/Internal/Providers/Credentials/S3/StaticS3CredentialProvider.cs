namespace Cntryl.Pants;

internal sealed class StaticS3CredentialProvider(S3Credentials credentials) : IS3CredentialProvider
{
    readonly S3Credentials _credentials = credentials;

    public ValueTask<S3Credentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_credentials);
    }
}
