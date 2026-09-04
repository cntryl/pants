namespace Cntryl.Pants.Cloud;

/// <summary>Controls a bounded, read-only cloud readiness preflight.</summary>
public sealed record PantsCloudPreflightOptions
{
    public static PantsCloudPreflightOptions Default { get; } = new(TimeSpan.FromSeconds(30));

    public PantsCloudPreflightOptions(TimeSpan deadline)
    {
        if (deadline < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument(
                "Cloud preflight deadline must be at least one millisecond.");
        }

        Deadline = deadline;
    }

    /// <summary>
    /// Gets the one absolute wall-clock budget shared by credential resolution and every read.
    /// </summary>
    public TimeSpan Deadline { get; }
}
