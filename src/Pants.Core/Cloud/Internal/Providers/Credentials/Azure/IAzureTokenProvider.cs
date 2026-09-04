namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Azure;

interface IAzureTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}
