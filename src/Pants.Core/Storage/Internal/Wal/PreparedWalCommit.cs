namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record PreparedWalCommit(
    ulong Sequence,
    IReadOnlyList<WalMutation> Mutations);
