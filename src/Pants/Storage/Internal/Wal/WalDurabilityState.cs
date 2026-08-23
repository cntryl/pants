namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalDurabilityState(
    int PendingWrites,
    long LastAppendedSequence,
    long LastSyncedSequence,
    long LocalDurableSequence);
