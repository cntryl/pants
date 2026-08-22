namespace Pants;

internal interface IAzureTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}
