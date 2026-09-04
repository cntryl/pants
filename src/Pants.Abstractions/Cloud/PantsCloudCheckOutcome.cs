namespace Cntryl.Pants.Cloud;

/// <summary>Describes the high-level result of one cloud check.</summary>
public enum PantsCloudCheckOutcome
{
    Passed,
    Failed,
    Warning,
    Unverified
}
