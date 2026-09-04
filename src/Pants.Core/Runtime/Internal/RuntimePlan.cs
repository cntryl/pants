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
        TransactionMemoryPoolBytes = options.Memory.TransactionMemoryPoolBytes ??
                                     Math.Max(1, MemoryBudgetBytes / 10);

        // Keep compaction and scan reservations bounded while preserving enough space for two
        // memtables. Tiny automatic configurations prioritize decoded compaction input blocks so
        // compaction can still make progress.
        var remainingAfterRequiredPools = Math.Max(
            0,
            MemoryBudgetBytes - TransactionMemoryPoolBytes - 2);
        var desiredCompactionPoolBytes = MemoryBudgetBytes < 1024 * 1024 &&
                                         options.Memory.MemtableSizeLimitBytes is null
            ? Math.Max(1, MemoryBudgetBytes * 2 / 3)
            : Math.Max(1, Math.Min(MemoryBudgetBytes / 10, 256L * 1024 * 1024));
        var desiredScanPoolBytes = Math.Max(
            1,
            Math.Min(MemoryBudgetBytes / 20, 128L * 1024 * 1024));
        if (options.Memory.MemtableSizeLimitBytes is > 0 and <= long.MaxValue / 2)
        {
            var unallocatedBytes = MemoryBudgetBytes -
                                   TransactionMemoryPoolBytes -
                                   2 * options.Memory.MemtableSizeLimitBytes.Value -
                                   desiredCompactionPoolBytes -
                                   desiredScanPoolBytes;
            desiredCompactionPoolBytes = checked(
                desiredCompactionPoolBytes +
                Math.Min(
                    Math.Max(0, unallocatedBytes),
                    256L * 1024 * 1024 - desiredCompactionPoolBytes));
        }

        CompactionMemoryPoolBytes = Math.Min(
            desiredCompactionPoolBytes,
            remainingAfterRequiredPools);
        ScanMemoryPoolBytes = Math.Min(
            desiredScanPoolBytes,
            remainingAfterRequiredPools - CompactionMemoryPoolBytes);

        var maximumMemtable =
            (MemoryBudgetBytes -
             TransactionMemoryPoolBytes -
             CompactionMemoryPoolBytes -
             ScanMemoryPoolBytes) / 2;
        var baseMemtable = PerformanceGoal switch
        {
            PantsPerformanceGoal.Latency => 64L * 1024 * 1024,
            PantsPerformanceGoal.Throughput => 256L * 1024 * 1024,
            PantsPerformanceGoal.Economy => 32L * 1024 * 1024,
            _ => throw PantsException.InvalidArgument("Unknown performance goal.")
        };
        var desiredMemtable = WorkloadProfile switch
        {
            PantsWorkloadProfile.WriteHeavy => baseMemtable * 2,
            PantsWorkloadProfile.ReadMostly => baseMemtable / 2,
            _ => baseMemtable
        };
        MemtableSizeLimitBytes = options.Memory.MemtableSizeLimitBytes ??
                                 Math.Max(1, Math.Min(desiredMemtable, maximumMemtable));
        MemtableFlushThresholdBytes = options.Memory.MemtableFlushThresholdBytes ??
                                      MemtableSizeLimitBytes;
        BlockCacheBytes = Math.Max(
            0,
            MemoryBudgetBytes -
            TransactionMemoryPoolBytes -
            CompactionMemoryPoolBytes -
            ScanMemoryPoolBytes -
            2 * MemtableSizeLimitBytes);
        if (PerformanceGoal == PantsPerformanceGoal.Economy)
        {
            BlockCacheBytes = Math.Min(BlockCacheBytes, 256L * 1024 * 1024);
        }

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
        ValidateMemoryPlan();
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

    void ValidateMemoryPlan()
    {
        if (TransactionMemoryPoolBytes > MemoryBudgetBytes)
        {
            throw PantsException.ResourceLimit("Transaction memory pool exceeds the total memory budget.");
        }

        if (MemtableSizeLimitBytes <= 0 || MemtableFlushThresholdBytes <= 0)
        {
            throw PantsException.InvalidArgument("Memtable limits must be greater than zero.");
        }

        if (WalBufferSizeBytes <= 0)
        {
            throw PantsException.InvalidArgument("WAL buffer size must be greater than zero.");
        }

        if (MemtableFlushThresholdBytes > MemtableSizeLimitBytes)
        {
            throw PantsException.InvalidArgument("Memtable flush threshold exceeds its size limit.");
        }

        long reservedBytes;
        try
        {
            reservedBytes = checked(
                2 * MemtableSizeLimitBytes +
                TransactionMemoryPoolBytes +
                CompactionMemoryPoolBytes +
                ScanMemoryPoolBytes);
        }
        catch (OverflowException)
        {
            throw PantsException.ResourceLimit(
                "Configured memory pools overflow the total memory budget calculation.");
        }

        if (reservedBytes > MemoryBudgetBytes)
        {
            throw PantsException.ResourceLimit(
                "Two memtables plus the transaction, compaction, and scan pools exceed the " +
                "total memory budget.");
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
