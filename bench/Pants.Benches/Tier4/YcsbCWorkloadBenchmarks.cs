namespace Cntryl.Pants.Tier4;

public class YcsbCWorkloadBenchmarks : YcsbSystemBenchmark
{
    static readonly YcsbScenario[] Cases = Enum.GetValues<Tier4StorageMode>()
        .SelectMany(mode => new[] { 1, 16, 64 }.Select(clients => new YcsbScenario(mode, clients)))
        .ToArray();

    protected override YcsbWorkload Workload => YcsbWorkload.C;

    public override IEnumerable<YcsbScenario> Scenarios => Cases;
}
