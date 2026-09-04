namespace Cntryl.Pants.Cloud;

public static class PantsCloudPreflightExtensions
{
    /// <summary>
    /// Performs a deadline-bounded, read-only LIST and, when possible, HEAD and one-byte read.
    /// This does not prove write authorization or durability.
    /// </summary>
    public static ValueTask<PantsCloudValidationReport> PreflightAsync(
        this PantsCloudStorageLocation location,
        PantsCloudPreflightOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CloudConfigurationPreflight.RunAsync(location, options, cancellationToken);

    /// <summary>
    /// Performs one deadline-bounded read-only preflight per unique physical location.
    /// </summary>
    public static ValueTask<PantsCloudValidationReport> PreflightAsync(
        this PantsCloudStorageTopology topology,
        PantsCloudPreflightOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CloudConfigurationPreflight.RunAsync(topology, options, cancellationToken);
}
