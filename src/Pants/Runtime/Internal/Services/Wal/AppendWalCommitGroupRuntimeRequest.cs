namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitGroupRuntimeRequest(
    IReadOnlyList<WalCommitGroupEntry> Commits,
    PantsRuntimeState State,
    PantsDurability Durability,
    PantsFailpoint BeforeSync) : WalRuntimeRequest;
