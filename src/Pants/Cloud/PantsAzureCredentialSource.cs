namespace Cntryl.Pants;

public abstract record PantsAzureCredentialSource
{
    PantsAzureCredentialSource()
    {
    }

    public sealed record SharedKey(string AccountKey) : PantsAzureCredentialSource
    {
        public override string ToString() => "SharedKey { AccountKey = [REDACTED] }";
    }

    public sealed record SasToken(string Token) : PantsAzureCredentialSource
    {
        public override string ToString() => "SasToken { Token = [REDACTED] }";
    }

    public sealed record ConnectionString(string Value) : PantsAzureCredentialSource
    {
        public override string ToString() => "ConnectionString { Value = [REDACTED] }";
    }

    public sealed record StorageEnvironment : PantsAzureCredentialSource;

    public sealed record EnvironmentClientSecret : PantsAzureCredentialSource;

    public sealed record WorkloadIdentity(
        string? TenantId = null,
        string? ClientId = null,
        string? TokenFile = null) : PantsAzureCredentialSource;

    public sealed record ManagedIdentity(string? ClientId = null) : PantsAzureCredentialSource;

    public sealed record LightweightDefaultChain : PantsAzureCredentialSource;
}
