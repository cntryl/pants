namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalLedgerMember(WalOpenFacts Open, WalSealedFacts? Sealed = null, ulong? DurableBytes = null);
