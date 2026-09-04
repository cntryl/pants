namespace Cntryl.Pants.Storage.Internal.Sst;

readonly record struct SstBlockHandle(ulong Offset, ulong Size);
