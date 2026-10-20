namespace Cntryl.Pants.Reporting;

sealed record MidgeBenchmarkResult(
    string ScenarioId,
    string Tier,
    string Workload,
    string Parameters,
    string PrimaryMetric,
    int MeasuredSamples,
    ulong OperationsCompleted,
    double Mean,
    string Quality,
    string TrustClass,
    string SourceSha,
    string Cpu,
    string OperatingSystem,
    string Runtime,
    string RunProfile,
    string ToolVersion)
{
    public string CargoLockSha256 { get; init; } = string.Empty;
}
