namespace Cntryl.Pants;

internal interface IAzureTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}
