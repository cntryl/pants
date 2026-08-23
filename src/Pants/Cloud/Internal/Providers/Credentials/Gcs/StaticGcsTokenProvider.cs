namespace Cntryl.Pants;

internal sealed class StaticGcsTokenProvider(string token) : IGcsTokenProvider
{
    readonly string _token = string.IsNullOrWhiteSpace(token)
        ? throw new PantsInvalidArgumentException("GCS bearer token must not be empty.")
        : token;

    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_token);
    }
}
