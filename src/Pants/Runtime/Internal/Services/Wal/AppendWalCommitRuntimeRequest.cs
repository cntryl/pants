namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitRuntimeRequest(
    CommitPayload Payload,
    PantsRuntimeState State,
    PantsDurability Durability) : WalRuntimeRequest;
