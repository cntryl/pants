namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed record HybridCacheMetrics(
    long MaximumLocalBytes,
    long TotalCommittedBytes,
    long FreeBytes,
    int UsagePercent,
    int PendingEvictions);
