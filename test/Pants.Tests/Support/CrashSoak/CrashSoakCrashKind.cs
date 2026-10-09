namespace Cntryl.Pants.Support.CrashSoak;

enum CrashSoakCrashKind
{
    /// <summary>Kill the child at the n-th hit of one engine failpoint, counting from open.</summary>
    Failpoint,

    /// <summary>Kill the child immediately after it dispatches its n-th commit, before acknowledgement.</summary>
    AfterDispatch,

    /// <summary>Kill the child after its whole workload has finished.</summary>
    EndOfWorkload
}
