namespace Pants;

internal sealed record S3Credentials(
    string AccessKey,
    string SecretKey,
    string? SessionToken);
