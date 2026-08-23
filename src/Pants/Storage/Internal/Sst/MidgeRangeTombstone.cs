namespace Cntryl.Pants;

internal sealed record MidgeRangeTombstone(byte[] Start, byte[] End, ulong Sequence);
