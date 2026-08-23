namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.S3;

interface IS3CredentialProvider
{
    ValueTask<S3Credentials> GetCredentialsAsync(CancellationToken cancellationToken);
}
