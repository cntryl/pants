namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record AppendWalCommitGroupRuntimeRequest(
    IReadOnlyList<WalCommitGroupEntry> Commits,
    RuntimeState State,
    PantsDurability Durability,
    Failpoint BeforeSync) : WalRuntimeRequest;
