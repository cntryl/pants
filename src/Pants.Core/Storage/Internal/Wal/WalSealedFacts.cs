namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalSealedFacts(WalChainLink Link, ulong RecordCount, ulong DataLen, ulong[][] MaxSeqPerCf);
