namespace Cntryl.Pants;

/// <summary>The effective open configuration after defaults and derived values are applied.</summary>
public sealed record PantsResolvedConfiguration(
    long MemoryBudgetBytes,
    long TransactionMemoryPoolBytes,
    long CompactionMemoryPoolBytes,
    long ScanMemoryPoolBytes,
    long MemtableSizeLimitBytes,
    long MemtableFlushThresholdBytes,
    long BlockCacheBytes,
    int BlockSizeBytes,
    long TargetSstSizeBytes,
    int WalBufferSizeBytes,
    int L0CompactionTrigger,
    TimeSpan StorageTimeout,
    TimeSpan RuntimeResponseTimeout,
    TimeSpan ShutdownTimeout)
{
    /// <summary>The active-snapshot age after which the runtime emits a lifetime warning.</summary>
    public TimeSpan LongLivedSnapshotWarningThreshold { get; init; } = TimeSpan.FromHours(1);
}
