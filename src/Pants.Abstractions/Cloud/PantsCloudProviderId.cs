namespace Cntryl.Pants.Cloud;

public readonly record struct PantsCloudProviderId
{
    public PantsCloudProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A cloud provider identifier is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static PantsCloudProviderId AwsS3 => new("aws-s3");

    public static PantsCloudProviderId S3Compatible => new("s3-compatible");

    public static PantsCloudProviderId AzureBlob => new("azure-blob");

    public static PantsCloudProviderId Gcs => new("gcs");

    public static PantsCloudProviderId OciObjectStorage => new("oci-object-storage");

    public override string ToString() => Value;
}
