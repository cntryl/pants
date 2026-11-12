namespace Cntryl.Pants.Transactions.Internal;

sealed class CommitPayload : IDisposable
{
    readonly ITransactionOperationSource _source;
    bool _prepared;
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
        _source = operations;
        Asserts = asserts;
    }

    public long TransactionId { get; }

    public PantsTransactionMode Mode { get; }

    public PantsConflictPolicy ConflictPolicy { get; }

    public DateTimeOffset SnapshotTime { get; }

    public DatabaseVersion StartSnapshot { get; }

    public ITransactionOperationSource Operations { get; private set; }

    public Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>> Asserts { get; }

    public bool IsSpilled => Operations.IsSpilled;

    public void PrepareOperations(DateTimeOffset commitTime)
    {
        if (!_prepared)
        {
            Operations = new PreparedTransactionOperationSource(_source, commitTime);
            _prepared = true;
        }
    }

    public void Dispose()
    {
        if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
