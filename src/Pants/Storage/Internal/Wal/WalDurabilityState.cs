namespace Cntryl.Pants;

readonly record struct WalDurabilityState(
    int PendingWrites,
    long LastAppendedSequence,
    long LastSyncedSequence,
    long LocalDurableSequence);
