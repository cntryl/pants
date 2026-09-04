namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitRuntimeRequest(
    CommitPayload Payload,
    RuntimeState State,
    PantsDurability Durability) : WalRuntimeRequest;
