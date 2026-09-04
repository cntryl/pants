namespace Cntryl.Pants.Options;

public sealed class PantsCloudCredentialOptions
{
    public PantsCloudCredentialKind Kind { get; set; } = PantsCloudCredentialKind.Default;

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string? SessionToken { get; set; }

    public string? Profile { get; set; }

    public string? CredentialsFile { get; set; }

    public string? ConfigFile { get; set; }

    public string? AccountKey { get; set; }

    public string? Token { get; set; }

    public string? ConnectionString { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? TokenFile { get; set; }

    public string? AccessId { get; set; }

    public string? Secret { get; set; }

    public string? Path { get; set; }
}
