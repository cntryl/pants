namespace Cntryl.Pants.Cloud.Internal.Objects;

abstract record CloudObjectWriteCondition
{
    CloudObjectWriteCondition()
    {
    }

    public sealed record Unconditional : CloudObjectWriteCondition;

    public sealed record IfAbsent : CloudObjectWriteCondition;

    public sealed record IfVersion(string Version) : CloudObjectWriteCondition;
}
