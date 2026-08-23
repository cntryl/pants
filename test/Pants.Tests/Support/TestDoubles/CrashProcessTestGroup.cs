namespace Cntryl.Pants.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrashProcessTestGroup
{
    public const string Name = "Crash process tests";
}
