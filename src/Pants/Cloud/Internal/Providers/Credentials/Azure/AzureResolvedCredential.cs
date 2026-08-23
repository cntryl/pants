namespace Pants;

internal sealed class AzureResolvedCredential
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
        tokenProvider: null);

    public static AzureResolvedCredential Sas(string token) => new(
        AzureResolvedCredentialKind.Sas,
        token.TrimStart('?'),
        tokenProvider: null);

    public static AzureResolvedCredential Bearer(IAzureTokenProvider tokenProvider) => new(
        AzureResolvedCredentialKind.Bearer,
        secret: null,
        tokenProvider);

    public override string ToString() => $"{Kind} {{ Credential = [REDACTED] }}";
}
