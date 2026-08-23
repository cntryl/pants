namespace Cntryl.Pants.Storage.Internal.Sst;

readonly record struct MidgeSstBlockHandle(ulong Offset, ulong Size);
