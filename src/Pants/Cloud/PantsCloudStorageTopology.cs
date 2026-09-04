namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudStorageTopology(
    PantsCloudStorageLocation Wal,
    PantsCloudStorageLocation Sst,
    PantsCloudStorageLocation Control)
{
    /// <summary>Validates each unique physical location without I/O.</summary>
    public PantsCloudValidationReport Validate() => CloudConfigurationValidator.Validate(this);

    /// <summary>
    /// Performs one deadline-bounded read-only preflight per unique physical location.
    /// </summary>
    public ValueTask<PantsCloudValidationReport> PreflightAsync(
        PantsCloudPreflightOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CloudConfigurationPreflight.RunAsync(this, options, cancellationToken);

    public static PantsCloudStorageTopology Shared(PantsCloudStorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new PantsCloudStorageTopology(location, location, location);
    }

    public PantsCloudStorageTopology WithWal(PantsCloudStorageLocation location) =>
        this with { Wal = location ?? throw new ArgumentNullException(nameof(location)) };

    public PantsCloudStorageTopology WithSst(PantsCloudStorageLocation location) =>
        this with { Sst = location ?? throw new ArgumentNullException(nameof(location)) };

    public PantsCloudStorageTopology WithControl(PantsCloudStorageLocation location) =>
        this with { Control = location ?? throw new ArgumentNullException(nameof(location)) };
}
