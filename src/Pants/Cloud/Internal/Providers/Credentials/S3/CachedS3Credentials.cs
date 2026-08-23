namespace Pants;

internal sealed record CachedS3Credentials(
    S3Credentials Credentials,
    DateTimeOffset? ExpiresAt)
{
    public bool RequiresRefresh(DateTimeOffset now) =>
        ExpiresAt is { } expiry && now.AddMinutes(5) >= expiry;

    public override string ToString() =>
        $"CachedS3Credentials {{ Credentials = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}
