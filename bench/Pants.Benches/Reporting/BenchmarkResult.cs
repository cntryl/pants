namespace Cntryl.Pants.Reporting;

sealed record BenchmarkResult(
    string ScenarioId,
    string Tier,
    string Workload,
    string Parameters,
    long OperationsPerInvoke,
    double MeanNanoseconds,
    double? ErrorNanoseconds,
    double AllocatedBytes,
    string MeasurementClass);
