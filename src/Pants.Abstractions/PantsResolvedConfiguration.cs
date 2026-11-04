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
    public PantsWriteBackpressureConfiguration WriteBackpressure { get; init; } = new();

    /// <summary>Total shared SST read allowance, including content and metadata.</summary>
    public long ReadResourceBudgetBytes { get; init; }

    /// <summary>The metadata quarter of the read allowance; scans share this owner.</summary>
    public long ReadMetadataBudgetBytes { get; init; }

    /// <summary>The optional aggregate startup budget, independent of storage I/O limits.</summary>
    public TimeSpan? OpenTimeout { get; init; }

    /// <summary>The active-snapshot age after which the runtime emits a lifetime warning.</summary>
    public TimeSpan LongLivedSnapshotWarningThreshold { get; init; } = TimeSpan.FromHours(1);
}
