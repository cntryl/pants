namespace Cntryl.Pants;

internal sealed record S3Credentials(
    string AccessKey,
    string SecretKey,
    string? SessionToken)
{
    public override string ToString() =>
        $"S3Credentials {{ AccessKey = [REDACTED], SecretKey = [REDACTED], SessionToken = {(SessionToken is null ? "<none>" : "[REDACTED]")} }}";
}
