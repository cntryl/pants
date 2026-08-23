namespace Cntryl.Pants;

internal abstract record CloudObjectDeleteCondition
{
    CloudObjectDeleteCondition()
    {
    }

    public sealed record Unconditional : CloudObjectDeleteCondition;

    public sealed record IfVersion(string Version) : CloudObjectDeleteCondition;
}
