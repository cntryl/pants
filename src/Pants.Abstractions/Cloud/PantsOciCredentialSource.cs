namespace Cntryl.Pants.Cloud;

/// <summary>Credential sources accepted by the OCI S3 Compatibility API transport.</summary>
public abstract record PantsOciCredentialSource
{
    PantsOciCredentialSource()
    {
    }

    public sealed record CustomerSecretKey(
        string AccessKey,
        string SecretKey) : PantsOciCredentialSource
    {
        public override string ToString() =>
            "CustomerSecretKey { AccessKey = [REDACTED], SecretKey = [REDACTED] }";
    }

    public sealed record Environment : PantsOciCredentialSource;

    public sealed record SharedProfile(
        string? Profile = null,
        string? CredentialsFile = null,
        string? ConfigFile = null) : PantsOciCredentialSource;

    /// <summary>
    ///     Represents the AWS-only default chain so validation can reject it deterministically.
    /// </summary>
    public sealed record AwsDefaultChain : PantsOciCredentialSource;
}
