namespace Cntryl.Pants.Runtime;

sealed record TestRuntimeRequest(
    int Sequence,
    bool ShouldWait,
    bool ShouldFail = false);
