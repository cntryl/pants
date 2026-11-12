namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalCommitGroupEntry(
    CommitPayload Payload,
    ulong ExpectedSequence);
