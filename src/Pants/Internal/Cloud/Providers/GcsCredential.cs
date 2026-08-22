namespace Pants;

internal sealed record GcsCredential(
    string? HmacAccessId,
    string? HmacSecret,
    IGcsTokenProvider? TokenProvider);
