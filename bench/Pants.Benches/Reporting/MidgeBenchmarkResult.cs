namespace Cntryl.Pants.Reporting;

sealed record MidgeBenchmarkResult(
    string ScenarioId,
    string Tier,
    string Workload,
    string Parameters,
    string PrimaryMetric,
    double Mean,
    string Quality,
    string TrustClass,
    string SourceSha,
    string Cpu,
    string OperatingSystem,
    string Runtime,
    string ToolVersion);
