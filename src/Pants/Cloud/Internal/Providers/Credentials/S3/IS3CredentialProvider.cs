namespace Cntryl.Pants;

internal interface IS3CredentialProvider
{
    ValueTask<S3Credentials> GetCredentialsAsync(CancellationToken cancellationToken);
}
