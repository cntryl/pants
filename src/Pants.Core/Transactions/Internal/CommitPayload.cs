namespace Cntryl.Pants.Transactions.Internal;

sealed class CommitPayload : IDisposable
{
    public CommitPayload(
        long transactionId,
        PantsTransactionMode mode,
        PantsConflictPolicy conflictPolicy,
        DateTimeOffset snapshotTime,
        DatabaseVersion startSnapshot,
        ITransactionOperationSource operations,
        Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> asserts)
    {
        TransactionId = transactionId;
        Mode = mode;
        ConflictPolicy = conflictPolicy;
        SnapshotTime = snapshotTime;
        StartSnapshot = startSnapshot;
        Operations = operations;
        Asserts = asserts;
    }

    public long TransactionId { get; }

    public PantsTransactionMode Mode { get; }

    public PantsConflictPolicy ConflictPolicy { get; }

    public DateTimeOffset SnapshotTime { get; }

    public DatabaseVersion StartSnapshot { get; }

    public ITransactionOperationSource Operations { get; }

    public Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> Asserts { get; }

    public bool IsSpilled => Operations.IsSpilled;

    public void Dispose()
    {
        if (Operations is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
