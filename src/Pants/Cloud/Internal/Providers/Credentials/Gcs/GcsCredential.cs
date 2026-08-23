namespace Cntryl.Pants;

internal sealed record GcsCredential(
    string? HmacAccessId,
    string? HmacSecret,
    IGcsTokenProvider? TokenProvider)
{
    public override string ToString() =>
        "GcsCredential { HmacAccessId = [REDACTED], HmacSecret = [REDACTED], TokenProvider = [REDACTED] }";
}
