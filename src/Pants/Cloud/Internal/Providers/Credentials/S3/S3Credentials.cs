namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.S3;

sealed record S3Credentials(
    string AccessKey,
    string SecretKey,
    string? SessionToken)
{
    public override string ToString() =>
        $"S3Credentials {{ AccessKey = [REDACTED], SecretKey = [REDACTED], SessionToken = {(SessionToken is null ? "<none>" : "[REDACTED]")} }}";
}
