namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record MidgeRangeTombstone(byte[] Start, byte[] End, ulong Sequence);
