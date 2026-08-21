namespace Pants;

internal abstract record CloudObjectWriteCondition
{
    private CloudObjectWriteCondition()
    {
    }

    public sealed record Unconditional : CloudObjectWriteCondition;

    public sealed record IfAbsent : CloudObjectWriteCondition;

    public sealed record IfVersion(string Version) : CloudObjectWriteCondition;
}
