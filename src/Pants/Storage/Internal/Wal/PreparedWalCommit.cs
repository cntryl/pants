namespace Cntryl.Pants;

sealed record PreparedWalCommit(
    long Sequence,
    byte[] Payload,
    IReadOnlyList<MidgeWalMutation> Mutations);
