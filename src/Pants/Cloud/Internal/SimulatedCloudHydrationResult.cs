namespace Cntryl.Pants.Cloud.Internal;

sealed record SimulatedCloudHydrationResult(
    ulong MinimumWriterEpoch,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RecoverySsts,
    bool RequiresSalvage);
