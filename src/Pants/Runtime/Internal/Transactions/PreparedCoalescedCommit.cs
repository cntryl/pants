namespace Cntryl.Pants.Runtime.Internal.Transactions;

sealed record PreparedCoalescedCommit(
    CommitRuntimeCommand Command,
    long Sequence,
    IReadOnlyList<TransactionIntentOperation> Operations,
    IReadOnlyDictionary<ColumnFamilyIdentity, long> BytesByFamily,
    IReadOnlyList<ColumnFamilyIdentity> Families);
