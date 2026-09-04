namespace Cntryl.Pants.Tier4;

public class YcsbAWorkloadBenchmarks : YcsbSystemBenchmark
{
    static readonly YcsbScenario[] Cases =
    [
        new(Tier4StorageMode.Memory, 1), new(Tier4StorageMode.Memory, 16), new(Tier4StorageMode.Memory, 64),
        new(Tier4StorageMode.Local, 64), new(Tier4StorageMode.SimulatedCloud, 64), new(Tier4StorageMode.Hybrid, 16)
    ];

    protected override YcsbWorkload Workload => YcsbWorkload.A;

    public override IEnumerable<YcsbScenario> Scenarios => Cases;
}
