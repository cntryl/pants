namespace Cntryl.Pants;

sealed record SimulatedCloudHydrationResult(
    ulong MinimumWriterEpoch,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RecoverySsts,
    bool RequiresSalvage);
