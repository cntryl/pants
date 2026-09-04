namespace Cntryl.Pants.Cloud;

/// <summary>Identifies the provider whose configuration or endpoint was checked.</summary>
public enum PantsCloudValidationProviderKind
{
    AwsS3,
    S3Compatible,
    AzureBlob,
    Gcs,
    OciObjectStorage
}
