namespace Pants;

public abstract record PantsS3CredentialSource
{
    private PantsS3CredentialSource()
    {
    }

    public sealed record StaticCredentials(
        string AccessKey,
        string SecretKey,
        string? SessionToken = null) : PantsS3CredentialSource
    {
        public override string ToString() =>
            $"Static {{ AccessKey = [REDACTED], SecretKey = [REDACTED], SessionToken = {(SessionToken is null ? "<none>" : "[REDACTED]")} }}";
    }

    public sealed record Environment : PantsS3CredentialSource;

    public sealed record SharedProfile(
        string? Profile = null,
        string? CredentialsFile = null,
        string? ConfigFile = null) : PantsS3CredentialSource;

    public sealed record AwsDefaultChain : PantsS3CredentialSource;
}
