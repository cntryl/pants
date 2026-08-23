namespace Cntryl.Pants.Storage.Internal.Hybrid;

sealed class HybridStorageBudgetPolicy
{
    public const long DefaultMaximumLocalBytes = 2L * 1024 * 1024 * 1024;
    public const int HighWatermarkPercent = 90;
    public const int CriticalWatermarkPercent = 95;
    public const int EmergencyWatermarkPercent = 98;

    public HybridStorageBudgetPolicy(long maximumLocalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLocalBytes);
        MaximumLocalBytes = maximumLocalBytes;
    }

    public long MaximumLocalBytes { get; }

    public int GetUsagePercent(long totalCommittedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCommittedBytes);
        var percent = (decimal)totalCommittedBytes * 100 / MaximumLocalBytes;
        return percent > int.MaxValue ? int.MaxValue : decimal.ToInt32(percent);
    }

    public long GetFreeBytes(long totalCommittedBytes) =>
        Math.Max(0, MaximumLocalBytes - Math.Min(totalCommittedBytes, MaximumLocalBytes));

    public HybridStorageWatermark GetWatermark(long totalCommittedBytes) =>
        GetUsagePercent(totalCommittedBytes) switch
        {
            >= EmergencyWatermarkPercent => HybridStorageWatermark.Emergency,
            >= CriticalWatermarkPercent => HybridStorageWatermark.Critical,
            >= HighWatermarkPercent => HybridStorageWatermark.High,
            _ => HybridStorageWatermark.Normal
        };
}
