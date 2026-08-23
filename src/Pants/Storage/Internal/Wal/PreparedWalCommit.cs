namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record PreparedWalCommit(
    long Sequence,
    byte[] Payload,
    IReadOnlyList<WalMutation> Mutations);
