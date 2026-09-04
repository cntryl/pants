namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record RangeTombstone(byte[] Start, byte[] End, ulong Sequence);
