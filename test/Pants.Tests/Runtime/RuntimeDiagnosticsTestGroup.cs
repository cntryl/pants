namespace Cntryl.Pants.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeDiagnosticsTestGroup
{
    public const string Name = nameof(RuntimeDiagnosticsTestGroup);
}
