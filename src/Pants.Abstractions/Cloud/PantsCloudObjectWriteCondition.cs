namespace Cntryl.Pants.Cloud;

public abstract record PantsCloudObjectWriteCondition
{
    PantsCloudObjectWriteCondition()
    {
    }

    public sealed record Unconditional : PantsCloudObjectWriteCondition;

    public sealed record IfAbsent : PantsCloudObjectWriteCondition;

    public sealed record IfVersion(string Version) : PantsCloudObjectWriteCondition;
}
