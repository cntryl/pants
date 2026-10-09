namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     Where one cycle's child is killed, and whether the verifier then discards the local cache so
///     recovery must rebuild from cloud objects alone.
/// </summary>
sealed record CrashSoakCrashPlan(
    CrashSoakCrashKind Kind,
    Failpoint? Failpoint,
    int Count,
    bool LoseLocalCache)
{
    public override string ToString()
    {
        var where = Kind switch
        {
            CrashSoakCrashKind.Failpoint => $"kill at hit {Count} of {Failpoint}",
            CrashSoakCrashKind.AfterDispatch => $"kill after dispatching commit {Count}",
            _ => "kill after the workload"
        };
        return LoseLocalCache ? $"{where}, then lose the local cache" : where;
    }
}
