namespace Cntryl.Pants.Tests.Runtime;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeDiagnosticsTestGroup
{
    public const string Name = nameof(RuntimeDiagnosticsTestGroup);
}
