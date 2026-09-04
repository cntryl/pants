namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudStorageLocation(
    PantsCloudProviderConfiguration Provider,
    string Prefix)
{
    /// <summary>Validates this location without resolving credentials or performing I/O.</summary>
    public PantsCloudValidationReport Validate() => CloudConfigurationValidator.Validate(this);

    /// <summary>
    /// Performs a deadline-bounded, read-only LIST and, when possible, HEAD and one-byte read.
    /// This does not prove write authorization or durability.
    /// </summary>
    public ValueTask<PantsCloudValidationReport> PreflightAsync(
        PantsCloudPreflightOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CloudConfigurationPreflight.RunAsync(this, options, cancellationToken);
}
