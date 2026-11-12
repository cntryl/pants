namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitRuntimeRequest(
    CommitPayload Payload,
    ulong PublishedSequence,
    PantsDurability Durability,
    OperationDeadline Deadline = default) : WalRuntimeRequest;
