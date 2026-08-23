namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Azure;

sealed class AzureResolvedCredential
{
    AzureResolvedCredential(
        AzureResolvedCredentialKind kind,
        string? secret,
        IAzureTokenProvider? tokenProvider)
    {
        Kind = kind;
        Secret = secret;
        TokenProvider = tokenProvider;
    }

    public AzureResolvedCredentialKind Kind { get; }

    public string? Secret { get; }

    public IAzureTokenProvider? TokenProvider { get; }

    public static AzureResolvedCredential SharedKey(string key) => new(
        AzureResolvedCredentialKind.SharedKey,
        key,
        null);

    public static AzureResolvedCredential Sas(string token) => new(
        AzureResolvedCredentialKind.Sas,
        token.TrimStart('?'),
        null);

    public static AzureResolvedCredential Bearer(IAzureTokenProvider tokenProvider) => new(
        AzureResolvedCredentialKind.Bearer,
        null,
        tokenProvider);

    public override string ToString() => $"{Kind} {{ Credential = [REDACTED] }}";
}
