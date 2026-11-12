namespace Cntryl.Pants.Runtime.Internal.Transactions;

sealed record PreparedCoalescedCommit(
    CommitRuntimeCommand Command,
    ulong Sequence,
    IReadOnlyList<TransactionIntentOperation> Operations,
    IReadOnlyDictionary<ColumnFamilyIdentity, long> BytesByFamily,
    IReadOnlyList<ColumnFamilyIdentity> Families);
