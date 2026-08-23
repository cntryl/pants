namespace Cntryl.Pants;

sealed record AppendWalCommitGroupRuntimeRequest(
    IReadOnlyList<WalCommitGroupEntry> Commits,
    PantsRuntimeState State,
    PantsDurability Durability,
    PantsFailpoint BeforeSync) : WalRuntimeRequest;
