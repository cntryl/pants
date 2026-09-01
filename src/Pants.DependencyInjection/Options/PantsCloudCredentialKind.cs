namespace Cntryl.Pants.DependencyInjection.Options;

public enum PantsCloudCredentialKind
{
    Default,
    S3Static,
    S3Environment,
    S3SharedProfile,
    AwsDefaultChain,
    AzureSharedKey,
    AzureSasToken,
    AzureConnectionString,
    AzureStorageEnvironment,
    AzureEnvironmentClientSecret,
    AzureWorkloadIdentity,
    AzureManagedIdentity,
    AzureLightweightDefaultChain,
    GcsBearerToken,
    GcsHmacKey,
    GcsApplicationDefault,
    GcsServiceAccountJsonFile,
    GcsAuthorizedUserJsonFile,
    GcsMetadataServer
}
