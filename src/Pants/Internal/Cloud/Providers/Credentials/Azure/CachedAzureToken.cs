namespace Pants;

internal sealed record CachedAzureToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    public bool RequiresRefresh(DateTimeOffset now) => now.AddMinutes(5) >= ExpiresAt;

    public override string ToString() =>
        $"CachedAzureToken {{ AccessToken = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}
