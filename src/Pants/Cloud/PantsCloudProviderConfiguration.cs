namespace Cntryl.Pants.Cloud;

public abstract record PantsCloudProviderConfiguration
{
    PantsCloudProviderConfiguration()
    {
    }

    /// <summary>
    /// Validates structure without network access, credential resolution, environment reads,
    /// file reads, or mutation.
    /// </summary>
    public PantsCloudValidationReport Validate() => CloudConfigurationValidator.Validate(this);

    public sealed record AwsS3(
        string Bucket,
        string Region,
        PantsS3CredentialSource Credentials) : PantsCloudProviderConfiguration
    {
        public override string ToString() =>
            $"AwsS3 {{ Bucket = {Bucket}, Region = {Region}, Credentials = {Credentials} }}";
    }

    public sealed record S3Compatible(
        string Bucket,
        string Region,
        Uri Endpoint,
        bool PathStyle,
        PantsS3CredentialSource Credentials) : PantsCloudProviderConfiguration
    {
        public override string ToString() =>
            $"S3Compatible {{ Bucket = {Bucket}, Region = {Region}, Endpoint = {Endpoint}, PathStyle = {PathStyle}, Credentials = {Credentials} }}";
    }

    public sealed record AzureBlob(
        string Account,
        string Container,
        Uri? Endpoint,
        PantsAzureCredentialSource Credential) : PantsCloudProviderConfiguration
    {
        public override string ToString() =>
            $"AzureBlob {{ Account = {Account}, Container = {Container}, Endpoint = {Endpoint}, Credential = {Credential} }}";
    }

    public sealed record Gcs(
        string Bucket,
        string ProjectId,
        Uri? Endpoint,
        PantsGcsApiStyle ApiStyle,
        PantsGcsCredentialSource Credential) : PantsCloudProviderConfiguration
    {
        public override string ToString() =>
            $"Gcs {{ Bucket = {Bucket}, ProjectId = {ProjectId}, Endpoint = {Endpoint}, ApiStyle = {ApiStyle}, Credential = {Credential} }}";
    }

    public sealed record OciObjectStorage(
        string Namespace,
        string Bucket,
        string Region,
        Uri? Endpoint,
        PantsOciCredentialSource Credentials) : PantsCloudProviderConfiguration
    {
        public Uri EffectiveEndpoint => Endpoint ?? new Uri(
            $"https://{Namespace}.compat.objectstorage.{Region}.oraclecloud.com");

        public override string ToString() =>
            $"OciObjectStorage {{ Namespace = {Namespace}, Bucket = {Bucket}, Region = {Region}, Endpoint = {Endpoint}, Credentials = {Credentials} }}";
    }
}
