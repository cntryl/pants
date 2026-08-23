namespace Pants;

internal sealed record GcsAccessToken(string Value, DateTimeOffset ExpiresAt)
{
    public bool RequiresRefresh(DateTimeOffset now) => now.AddMinutes(5) >= ExpiresAt;

    public override string ToString() =>
        $"GcsAccessToken {{ Value = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}
