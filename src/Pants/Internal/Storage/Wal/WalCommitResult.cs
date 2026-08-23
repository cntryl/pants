namespace Pants;

readonly record struct WalCommitResult(Exception? PostDurabilityFailure);
