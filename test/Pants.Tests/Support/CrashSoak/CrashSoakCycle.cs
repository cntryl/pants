namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>One child process's workload and kill point, a pure function of the seed and cycle index.</summary>
sealed record CrashSoakCycle(
    int Index,
    IReadOnlyList<IReadOnlyList<CrashSoakOperation>> Lanes,
    CrashSoakCrashPlan CrashPlan)
{
    public IEnumerable<CrashSoakOperation> Operations => Lanes.SelectMany(static lane => lane);
}
