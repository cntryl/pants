namespace Cntryl.Pants;

internal sealed class CommitPayload
{
    public CommitPayload(
        long transactionId,
        PantsTransactionMode mode,
        PantsConflictPolicy conflictPolicy,
        DateTimeOffset snapshotTime,
        DatabaseSnapshot startSnapshot,
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

    public DatabaseSnapshot StartSnapshot { get; }

    public ITransactionOperationSource Operations { get; }

    public Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> Asserts { get; }

    public bool IsSpilled => Operations.IsSpilled;
}
