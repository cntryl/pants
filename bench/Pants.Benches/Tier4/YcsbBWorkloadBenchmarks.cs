namespace Cntryl.Pants.Benches.Tier4;

public class YcsbBWorkloadBenchmarks : YcsbSystemBenchmark
{
    static readonly YcsbScenario[] Cases = CreateCases();

    protected override YcsbWorkload Workload => YcsbWorkload.B;

    public override IEnumerable<YcsbScenario> Scenarios => Cases;

    static YcsbScenario[] CreateCases() =>
    [
        new(Tier4StorageMode.Local, 1), new(Tier4StorageMode.Local, 16), new(Tier4StorageMode.Local, 64),
        new(Tier4StorageMode.SimulatedCloud, 1), new(Tier4StorageMode.SimulatedCloud, 16),
        new(Tier4StorageMode.SimulatedCloud, 64)
    ];
}
