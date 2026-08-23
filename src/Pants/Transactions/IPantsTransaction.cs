namespace Cntryl.Pants.Transactions;

public interface IPantsTransaction : IAsyncDisposable
{
    IPantsColumnFamily ColumnFamily { get; }

    PantsTransactionMode Mode { get; }

    PantsConflictPolicy ConflictPolicy { get; }

    void SetConflictPolicy(PantsConflictPolicy conflictPolicy);

    void Put(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null);

    void Insert(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null);

    void Delete(ReadOnlyMemory<byte> key);

    void DeleteRange(
        ReadOnlyMemory<byte> startInclusive,
        ReadOnlyMemory<byte> endExclusive);

    void AssertValue(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte>? expectedValue);

    ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default);

    ValueTask<PantsPointReadResult> GetWithDiagnosticsAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default);

    ValueTask<IPantsScan> ScanAsync(
        PantsScanQuery query,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        PantsWriteOptions options,
        CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
