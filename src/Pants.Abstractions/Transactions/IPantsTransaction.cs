namespace Cntryl.Pants.Transactions;

/// <summary>
///     A transaction over a fixed database snapshot. Dispose the transaction after commit or
///     rollback; disposing an active transaction rolls it back and releases its snapshot and spill
///     resources. An undisposed transaction continues to pin its snapshot, can retain obsolete
///     storage, and blocks database shutdown.
/// </summary>
public interface IPantsTransaction : IAsyncDisposable
{
    IPantsColumnFamily ColumnFamily { get; }

    PantsTransactionMode Mode { get; }

    PantsConflictPolicy ConflictPolicy { get; }

    /// <summary>
    ///     Selects write-set conflict handling. This does not turn ordinary reads or scans into assertions.
    /// </summary>
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

    /// <summary>
    ///     Requires the key to match <paramref name="expectedValue" /> at the transaction snapshot and
    ///     remain unchanged through commit. A null value asserts that the key is absent.
    /// </summary>
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
