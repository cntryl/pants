namespace Pants;

sealed record AppendWalCommitRuntimeRequest(
    CommitPayload Payload,
    PantsRuntimeState State,
    PantsDurability Durability) : WalRuntimeRequest;
