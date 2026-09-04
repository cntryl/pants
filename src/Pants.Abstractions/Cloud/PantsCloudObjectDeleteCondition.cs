namespace Cntryl.Pants.Cloud;

public abstract record PantsCloudObjectDeleteCondition
{
    PantsCloudObjectDeleteCondition()
    {
    }

    public sealed record Unconditional : PantsCloudObjectDeleteCondition;

    public sealed record IfVersion(string Version) : PantsCloudObjectDeleteCondition;
}
