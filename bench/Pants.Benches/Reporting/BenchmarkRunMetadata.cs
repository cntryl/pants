namespace Cntryl.Pants.Reporting;

sealed record BenchmarkRunMetadata(
    string Engine,
    string SourceSha,
    string OperatingSystem,
    string Architecture,
    string Cpu,
    string Runtime,
    string MeasurementClass,
    int ExpectedScenarioCount);
