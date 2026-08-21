namespace Pants;

internal sealed class CommitPayload
{
    public CommitPayload(
        long transactionId,
        PantsTransactionMode mode,
        PantsConflictPolicy conflictPolicy,
        DateTimeOffset snapshotTime,
        DatabaseSnapshot startSnapshot,
        IReadOnlyList<TransactionIntentOperation> orderedOperations,
        Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionPendingWrite>> writes,
        Dictionary<ColumnFamilyIdentity, List<DeleteRange>> deleteRanges,
        Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionReadValue>> reads,
        Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> asserts)
    {
        TransactionId = transactionId;
        Mode = mode;
        ConflictPolicy = conflictPolicy;
        SnapshotTime = snapshotTime;
        StartSnapshot = startSnapshot;
        OrderedOperations = orderedOperations;
        Writes = writes;
        DeleteRanges = deleteRanges;
        Reads = reads;
        Asserts = asserts;
    }

    public long TransactionId { get; }

    public PantsTransactionMode Mode { get; }

    public PantsConflictPolicy ConflictPolicy { get; }

    public DateTimeOffset SnapshotTime { get; }

    public DatabaseSnapshot StartSnapshot { get; }

    public IReadOnlyList<TransactionIntentOperation> OrderedOperations { get; }

    public Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionPendingWrite>> Writes { get; }

    public Dictionary<ColumnFamilyIdentity, List<DeleteRange>> DeleteRanges { get; }

    public Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionReadValue>> Reads { get; }

    public Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> Asserts { get; }
}
