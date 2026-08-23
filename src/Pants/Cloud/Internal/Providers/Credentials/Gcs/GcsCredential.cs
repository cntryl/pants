namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Gcs;

sealed record GcsCredential(
    string? HmacAccessId,
    string? HmacSecret,
    IGcsTokenProvider? TokenProvider)
{
    public override string ToString() =>
        "GcsCredential { HmacAccessId = [REDACTED], HmacSecret = [REDACTED], TokenProvider = [REDACTED] }";
}
