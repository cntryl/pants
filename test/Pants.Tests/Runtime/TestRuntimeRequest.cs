namespace Cntryl.Pants.Tests.Runtime;

sealed record TestRuntimeRequest(
    int Sequence,
    bool ShouldWait,
    bool ShouldFail = false);
