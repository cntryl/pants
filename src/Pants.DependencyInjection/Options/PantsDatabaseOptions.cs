using Cntryl.Pants.Storage;

namespace Cntryl.Pants.Options;

/// <summary>Bindable host settings used to construct immutable database-open options.</summary>
public sealed class PantsDatabaseOptions
{
    public PantsStorageOptions Storage { get; set; } = new();

    public PantsPerformanceGoal PerformanceGoal { get; set; } = PantsPerformanceGoal.Latency;

    public long? MemoryBudgetBytes { get; set; }

    public PantsWorkloadProfile WorkloadProfile { get; set; } = PantsWorkloadProfile.Mixed;

    public PantsRecoveryPolicy RecoveryPolicy { get; set; } = PantsRecoveryPolicy.Strict;

    public PantsBlockCachePolicy BlockCachePolicy { get; set; } = PantsBlockCachePolicy.Lru;

    public PantsCloudWriteOptions? CloudWritePolicy { get; set; }

    public PantsWriteBackpressureConfiguration WriteBackpressure { get; set; } = new();

    public TimeSpan StorageTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan? RuntimeResponseTimeout { get; set; }

    public TimeSpan LongLivedSnapshotWarningThreshold { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool BackgroundCompaction { get; set; } = true;

    public long? MemtableSizeLimitBytes { get; set; }

    public long? MemtableFlushThresholdBytes { get; set; }

    public long? TransactionMemoryPoolBytes { get; set; }

    public int? WalBufferSizeBytes { get; set; }

    public TimeSpan LeaseTimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Null derives the clock-skew tolerance from half the configured TTL.</summary>
    public TimeSpan? LeaseClockSkewTolerance { get; set; }

    public ulong MinimumEpoch { get; set; }

    public PantsCompactionOptions? Compaction { get; set; }
}
