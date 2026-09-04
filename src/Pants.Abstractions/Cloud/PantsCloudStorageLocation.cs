namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudStorageLocation(
    PantsCloudProviderConfiguration Provider,
    string Prefix)
{
    /// <summary>Validates this location without resolving credentials or performing I/O.</summary>
    public PantsCloudValidationReport Validate() => CloudConfigurationValidator.Validate(this);

}
