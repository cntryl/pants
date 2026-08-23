namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Azure;

sealed record AzureCredentialResolution(
    string Account,
    Uri Endpoint,
    AzureResolvedCredential Credential);
