namespace Pants;

internal sealed record AzureCredentialResolution(
    string Account,
    Uri Endpoint,
    AzureResolvedCredential Credential);
