namespace Cntryl.Pants;

public interface IPantsDatabaseDiagnostics
{
    ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsReadPathDiagnostics> GetReadPathDiagnosticsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsStorageLayout> GetStorageLayoutAsync(
        CancellationToken cancellationToken = default);
}
