namespace Cntryl.Pants.Benches.Tier4;

public class YcsbDWorkloadBenchmarks : YcsbSystemBenchmark
{
    static readonly YcsbScenario[] Cases =
    [
        new(Tier4StorageMode.Local, 1), new(Tier4StorageMode.Local, 16), new(Tier4StorageMode.Local, 64),
        new(Tier4StorageMode.SimulatedCloud, 1), new(Tier4StorageMode.SimulatedCloud, 16),
        new(Tier4StorageMode.SimulatedCloud, 64)
    ];

    protected override YcsbWorkload Workload => YcsbWorkload.D;

    public override IEnumerable<YcsbScenario> Scenarios => Cases;
}
