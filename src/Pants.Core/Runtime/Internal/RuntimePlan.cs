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
        OpenTimeout = options.Runtime.OpenTimeout;
        RuntimeResponseTimeout = options.Runtime.RuntimeResponseTimeout ??
                                 DeriveRuntimeResponseTimeout(StorageTimeout);
        LongLivedSnapshotWarningThreshold = options.Runtime.LongLivedSnapshotWarningThreshold;
        LeaseTimeToLive = options.Lease.TimeToLive;
        LeaseClockSkewTolerance = options.Lease.ClockSkewTolerance;
        MinimumEpoch = options.Lease.MinimumEpoch;
        LeaseLossCallback = options.Lease.LossCallback;
        CoordinatorQueueCapacity = options.CoordinatorQueueCapacity;
        FlushAfterWalRecords = options.FlushAfterWalRecords;

        MemoryBudgetBytes = ResolveMemoryBudget(options.Memory.Budget);
        MaximumReplayableTransactionBytes = Storage switch
        {
            PantsStorageConfiguration.Cloud cloud => Math.Min(MemoryBudgetBytes / 16,
                (cloud.LocalStorageBudgetBytes ?? HybridStorageBudgetPolicy.DefaultMaximumLocalBytes) / 2),
            PantsStorageConfiguration.SimulatedCloud simulated => Math.Min(MemoryBudgetBytes / 16,
                (simulated.LocalStorageBudgetBytes ?? HybridStorageBudgetPolicy.DefaultMaximumLocalBytes) / 2),
            _ => null
        };
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
        ReadResourceBudgetBytes = pools.BlockCacheBytes;
        ReadMetadataBudgetBytes = ReadResourceBudgetBytes / 4;
        BlockCacheBytes = ReadResourceBudgetBytes - ReadMetadataBudgetBytes;

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
        WriteBackpressure = options.WriteBackpressure;
        ValidateWriteBackpressure();
        ValidateWalBuffer();
    }

    public PantsStorageConfiguration Storage { get; }

    public PantsPerformanceGoal PerformanceGoal { get; }

    public PantsWorkloadProfile WorkloadProfile { get; }

    public PantsRecoveryPolicy RecoveryPolicy { get; }

    public PantsBlockCachePolicy BlockCachePolicy { get; }

    public PantsCloudWritePolicy CloudWritePolicy { get; }

    public TimeSpan StorageTimeout { get; }

    public TimeSpan? OpenTimeout { get; }

    public TimeSpan RuntimeResponseTimeout { get; }

    public TimeSpan LongLivedSnapshotWarningThreshold { get; }

    public long MemoryBudgetBytes { get; }

    public long? MaximumReplayableTransactionBytes { get; }

    public long TransactionMemoryPoolBytes { get; }

    public long CompactionMemoryPoolBytes { get; }

    public long ScanMemoryPoolBytes { get; }

    public long MemtableSizeLimitBytes { get; }

    public long MemtableFlushThresholdBytes { get; }

    public long FlushWorkingMemoryLimitBytes => checked(
        4 * Math.Max(MemtableSizeLimitBytes, MemoryBudgetBytes / 8) + 1024L * 1024);

    public long BlockCacheBytes { get; }

    public long ReadResourceBudgetBytes { get; }

    public long ReadMetadataBudgetBytes { get; }

    public int BlockSizeBytes { get; }

    public long TargetSstSizeBytes { get; }

    public int WalBufferSizeBytes { get; }

    public int L0CompactionTrigger { get; }

    public PantsCompactionConfiguration Compaction { get; }

    public PantsWriteBackpressureConfiguration WriteBackpressure { get; }

    public bool BackgroundCompaction => Compaction.BackgroundEnabled;

    public TimeSpan LeaseTimeToLive { get; }

    public TimeSpan LeaseClockSkewTolerance { get; }

    public ulong MinimumEpoch { get; }

    public Action? LeaseLossCallback { get; }

    public int CoordinatorQueueCapacity { get; }

    public int FlushAfterWalRecords { get; }

    public TimeSpan LeaseHeartbeatInterval => TimeSpan.FromTicks(Math.Max(1, LeaseTimeToLive.Ticks / 3));

    public static RuntimePlan Resolve(PantsOpenOptions options) => new(options);

    void ValidateWalBuffer()
    {
        if (WalBufferSizeBytes <= 0)
        {
            throw PantsException.InvalidArgument("WAL buffer size must be greater than zero.");
        }
    }

    void ValidateWriteBackpressure()
    {
        var policy = WriteBackpressure;
        var hasSoft = policy.L0SoftLimit is not null || policy.MemorySoftLimitBytes is not null ||
                      policy.PendingCompactionSoftLimitBytes is not null;
        var memoryHard = MemtableFlushThresholdBytes > long.MaxValue / 2
            ? long.MaxValue
            : MemtableFlushThresholdBytes * 2;
        if (policy.L0SoftLimit is { } l0 && (l0 <= 0 || l0 >= (long)Compaction.L0FileCountTrigger + 11) ||
            policy.MemorySoftLimitBytes is { } memory && (memory <= 0 || memory >= memoryHard) ||
            policy.PendingCompactionSoftLimitBytes is { } pending &&
            (pending == 0 || policy.PendingCompactionHardLimitBytes is not { } hard || pending >= hard) ||
            policy.PendingCompactionHardLimitBytes is 0 || policy.BackgroundWriteBytesPerSecond is 0 ||
            policy.MaximumAdmissionDelay < TimeSpan.Zero ||
            hasSoft && policy.MaximumAdmissionDelay == TimeSpan.Zero ||
            policy.MaximumAdmissionDelay.TotalSeconds * TimeProvider.System.TimestampFrequency >
            long.MaxValue - (double)TimeProvider.System.GetTimestamp())
        {
            throw PantsException.InvalidArgument(
                "Write soft limits must be positive and below resolved hard limits; enabled slowdown " +
                "and maintenance rates must be positive and representable by the monotonic clock.");
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
