namespace Cntryl.Pants.Cloud.Internal;

sealed record SimulatedCloudHydrationResult(
    ulong MinimumWriterEpoch,
    bool RequiresSalvage,
    ulong NextWalSegmentId = 1);
