namespace Cntryl.Pants.Tests;

sealed record TestRuntimeRequest(
    int Sequence,
    bool ShouldWait,
    bool ShouldFail = false);
