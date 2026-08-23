namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalCommitResult(Exception? PostDurabilityFailure);
