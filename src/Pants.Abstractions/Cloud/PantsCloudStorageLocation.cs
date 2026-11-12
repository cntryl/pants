namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudStorageLocation(
    IPantsCloudProvider Provider,
    string Prefix)
{
    string _prefix = Prefix?.Trim('/')!;

    public string Prefix
    {
        get => _prefix;
        init => _prefix = value?.Trim('/')!;
    }

    /// <summary>Validates this location without resolving credentials or performing I/O.</summary>
    public PantsCloudValidationReport Validate() => CloudConfigurationValidator.Validate(this);
}
