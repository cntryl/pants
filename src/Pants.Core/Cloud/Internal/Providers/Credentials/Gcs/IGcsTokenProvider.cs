namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Gcs;

interface IGcsTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}
