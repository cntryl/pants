namespace Cntryl.Pants.Runtime.Internal;

/// <summary>Resolved, executable policy derived from immutable public open options.</summary>
sealed class RuntimePlan
{
    const long FallbackMemoryBudgetBytes = 512L * 1024 * 1024;

    RuntimePlan(PantsOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Storage = options.Storage;
        PerformanceGoal = options.Runtime.PerformanceGoal;
        WorkloadProfile = options.Runtime.WorkloadProfile;
        RecoveryPolicy = options.RecoveryPolicy;
        BlockCachePolicy = options.Memory.BlockCachePolicy;
        CloudWritePolicy = options.CloudWritePolicy;
        StorageTimeout = options.Runtime.StorageTimeout;
        RuntimeResponseTimeout = options.Runtime.RuntimeResponseTimeout ??
                                 DeriveRuntimeResponseTimeout(StorageTimeout);
        LeaseTimeToLive = options.Lease.TimeToLive;
        LeaseClockSkewTolerance = options.Lease.ClockSkewTolerance;
        MinimumEpoch = options.Lease.MinimumEpoch;
        LeaseLossCallback = options.Lease.LossCallback;
        CoordinatorQueueCapacity = options.CoordinatorQueueCapacity;
        FlushAfterWalRecords = options.FlushAfterWalRecords;

        MemoryBudgetBytes = ResolveMemoryBudget(options.Memory.Budget);
        var pools = MemoryPools.Derive(
            MemoryBudgetBytes,
            options.Memory,
            Storage,
            PerformanceGoal,
            WorkloadProfile);
        TransactionMemoryPoolBytes = pools.TransactionMemoryPoolBytes;
        CompactionMemoryPoolBytes = pools.CompactionMemoryPoolBytes;
        ScanMemoryPoolBytes = pools.ScanMemoryPoolBytes;
        MemtableSizeLimitBytes = pools.MemtableSizeLimitBytes;
        MemtableFlushThresholdBytes = pools.MemtableFlushThresholdBytes;
        BlockCacheBytes = pools.BlockCacheBytes;

        BlockSizeBytes = (PerformanceGoal, WorkloadProfile) switch
        {
            (PantsPerformanceGoal.Latency, _) => 16 * 1024,
            (PantsPerformanceGoal.Economy, _) => 32 * 1024,
            (PantsPerformanceGoal.Throughput, PantsWorkloadProfile.RangeScan) => 128 * 1024,
            _ => 64 * 1024
        };
        TargetSstSizeBytes = PerformanceGoal switch
        {
            PantsPerformanceGoal.Latency => 128L * 1024 * 1024,
            PantsPerformanceGoal.Throughput => 512L * 1024 * 1024,
            _ => 256L * 1024 * 1024
        };
        WalBufferSizeBytes = options.Memory.WalBufferSizeBytes ??
                             checked((int)Math.Clamp(
                                 PerformanceGoal switch
                                 {
                                     PantsPerformanceGoal.Latency => 128L * 1024,
                                     PantsPerformanceGoal.Throughput => 1024L * 1024,
                                     _ => 256L * 1024
                                 },
                                 1,
                                 MemoryBudgetBytes));
        L0CompactionTrigger = (PerformanceGoal, WorkloadProfile) switch
        {
            (PantsPerformanceGoal.Latency, _) => 3,
            (_, PantsWorkloadProfile.WriteHeavy) => 8,
            (PantsPerformanceGoal.Throughput, _) => 6,
            _ => 4
        };
        Compaction = (options.ConfiguredCompaction ?? new PantsCompactionConfiguration(
                L0FileCountTrigger: L0CompactionTrigger)) with
        {
            BackgroundEnabled = options.BackgroundCompaction
        };
        TargetSstSizeBytes = Compaction.TargetSstSizeBytes ?? TargetSstSizeBytes;
        ValidateWalBuffer();
    }

    public PantsStorageConfiguration Storage { get; }

    public PantsPerformanceGoal PerformanceGoal { get; }

    public PantsWorkloadProfile WorkloadProfile { get; }

    public PantsRecoveryPolicy RecoveryPolicy { get; }

    public PantsBlockCachePolicy BlockCachePolicy { get; }

    public PantsCloudWritePolicy CloudWritePolicy { get; }

    public TimeSpan StorageTimeout { get; }

    public TimeSpan RuntimeResponseTimeout { get; }

    public long MemoryBudgetBytes { get; }

    public long TransactionMemoryPoolBytes { get; }

    public long CompactionMemoryPoolBytes { get; }

    public long ScanMemoryPoolBytes { get; }

    public long MemtableSizeLimitBytes { get; }

    public long MemtableFlushThresholdBytes { get; }

    public long BlockCacheBytes { get; }

    public int BlockSizeBytes { get; }

    public long TargetSstSizeBytes { get; }

    public int WalBufferSizeBytes { get; }

    public int L0CompactionTrigger { get; }

    public PantsCompactionConfiguration Compaction { get; }

    public bool BackgroundCompaction => Compaction.BackgroundEnabled;

    public TimeSpan LeaseTimeToLive { get; }

    public TimeSpan LeaseClockSkewTolerance { get; }

    public ulong MinimumEpoch { get; }

    public Action? LeaseLossCallback { get; }

    public int CoordinatorQueueCapacity { get; }

    public int FlushAfterWalRecords { get; }

    public TimeSpan LeaseHeartbeatInterval => TimeSpan.FromTicks(Math.Clamp(
        LeaseTimeToLive.Ticks / 3,
        TimeSpan.TicksPerMillisecond,
        TimeSpan.FromSeconds(10).Ticks));

    public static RuntimePlan Resolve(PantsOpenOptions options) => new(options);

    void ValidateWalBuffer()
    {
        if (WalBufferSizeBytes <= 0)
        {
            throw PantsException.InvalidArgument("WAL buffer size must be greater than zero.");
        }
    }

    static long ResolveMemoryBudget(PantsMemoryBudget budget)
    {
        if (budget.Bytes is { } bytes)
        {
            return bytes;
        }

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? Math.Max(5, available / 2) : FallbackMemoryBudgetBytes;
    }

    static TimeSpan DeriveRuntimeResponseTimeout(TimeSpan storageTimeout)
    {
        const long marginTicks = TimeSpan.TicksPerSecond * 30;
        var derivedTicks = storageTimeout.Ticks > TimeSpan.MaxValue.Ticks - marginTicks
            ? TimeSpan.MaxValue.Ticks
            : storageTimeout.Ticks + marginTicks;
        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(60).Ticks, derivedTicks));
    }
}
