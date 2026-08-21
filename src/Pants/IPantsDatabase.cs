namespace Pants;

public interface IPantsDatabase : IAsyncDisposable
{
    PantsOpenOptions Options { get; }

    IPantsColumnFamily DefaultColumnFamily { get; }

    bool IsPrimaryLeaseHealthy { get; }

    ValueTask<IPantsColumnFamily> CreateColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<IPantsColumnFamily?> GetColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IPantsColumnFamily>> ListColumnFamiliesAsync(
        CancellationToken cancellationToken = default);

    ValueTask DropColumnFamilyAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);

    ValueTask DropColumnFamilyDiscardingUnflushedAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);

    ValueTask<IPantsTransaction> BeginTransactionAsync(
        IPantsColumnFamily columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken = default);

    ValueTask FlushAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);

    ValueTask CompactAllAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WaitForWriteStallClearAsync(
        IPantsColumnFamily columnFamily,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsStorageLayout> GetStorageLayoutAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PantsStorageVerificationReport> VerifyStorageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
