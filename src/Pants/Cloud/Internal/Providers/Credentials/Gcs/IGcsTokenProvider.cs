namespace Pants;

internal interface IGcsTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}
