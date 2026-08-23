namespace Cntryl.Pants;

readonly record struct WalCommitResult(Exception? PostDurabilityFailure);
