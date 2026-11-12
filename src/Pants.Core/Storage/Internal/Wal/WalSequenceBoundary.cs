namespace Cntryl.Pants.Storage.Internal.Wal;

readonly record struct WalSequenceBoundary(ulong PreviousLastSequence, ulong FirstSequence);
