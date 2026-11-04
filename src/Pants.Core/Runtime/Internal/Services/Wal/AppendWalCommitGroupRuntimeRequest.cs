namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitGroupRuntimeRequest(
    IReadOnlyList<WalCommitGroupEntry> Commits,
    ulong PublishedSequence,
    PantsDurability Durability,
    Failpoint BeforeSync,
    OperationDeadline Deadline = default) : WalRuntimeRequest;
