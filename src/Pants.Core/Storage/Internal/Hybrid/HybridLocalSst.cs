namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed record HybridLocalSst(
    string Name,
    long SizeBytes);
