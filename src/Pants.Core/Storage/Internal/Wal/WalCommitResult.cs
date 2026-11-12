namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalCommitResult(ulong CommitSequence, Exception? PostDurabilityFailure = null);
