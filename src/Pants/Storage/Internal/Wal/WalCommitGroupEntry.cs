namespace Pants;

readonly record struct WalCommitGroupEntry(
    CommitPayload Payload,
    long ExpectedSequence);
