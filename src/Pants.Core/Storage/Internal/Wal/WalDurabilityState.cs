namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalDurabilityState(
    int PendingWrites,
    ulong LastAppendedSequence,
    ulong LastSyncedSequence,
    ulong LocalDurableSequence);
