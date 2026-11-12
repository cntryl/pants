namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalOpenFacts(ulong Id, ulong Epoch, ulong FirstSeq, WalChainLink Prev);
