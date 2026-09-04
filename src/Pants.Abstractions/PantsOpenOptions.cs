namespace Cntryl.Pants;

/// <summary>Immutable database-open configuration.</summary>
public sealed class PantsOpenOptions
{
    static readonly TimeSpan MinimumLeaseTimeToLive = TimeSpan.FromMilliseconds(3);
    readonly Configuration _configuration;

    PantsOpenOptions(Configuration configuration)
    {
        _configuration = configuration;
        Runtime = new PantsRuntimeConfiguration(
            configuration.PerformanceGoal,
            configuration.WorkloadProfile,
            configuration.StorageTimeout,
            configuration.RuntimeResponseTimeout,
            configuration.ShutdownTimeout);
        Memory = new PantsMemoryConfiguration(
            configuration.MemoryBudget,
            configuration.BlockCachePolicy,
            configuration.MemtableSizeLimitBytes,
            configuration.MemtableFlushThresholdBytes,
            configuration.TransactionMemoryPoolBytes,
            configuration.WalBufferSizeBytes);
        Lease = new PantsLeaseConfiguration(
            configuration.LeaseTimeToLive,
            configuration.LeaseClockSkewTolerance,
            configuration.MinimumEpoch,
            configuration.LeaseLossCallback);
        Compaction = (configuration.Compaction ?? new PantsCompactionConfiguration()) with
        {
            BackgroundEnabled = configuration.BackgroundCompaction
        };
        ValidateRawConfiguration();
    }

    public PantsStorageConfiguration Storage => _configuration.Storage;

    public PantsRuntimeConfiguration Runtime { get; }

    public PantsMemoryConfiguration Memory { get; }

    public PantsLeaseConfiguration Lease { get; }

    public PantsRecoveryPolicy RecoveryPolicy => _configuration.RecoveryPolicy;

    public PantsCloudWritePolicy CloudWritePolicy => _configuration.CloudWritePolicy;

    public PantsCompactionConfiguration Compaction { get; }

    public IPantsClock TtlClock => _configuration.TtlClock;

    internal PantsCompactionConfiguration? ConfiguredCompaction => _configuration.Compaction;

    internal bool BackgroundCompaction => _configuration.BackgroundCompaction;

    internal int CoordinatorQueueCapacity => _configuration.CoordinatorQueueCapacity;

    internal int FlushAfterWalRecords => _configuration.FlushAfterWalRecords;

    public static PantsOpenOptions InMemory() =>
        new(Configuration.Default(new PantsStorageConfiguration.InMemory()));

    public static PantsOpenOptions Create(
        PantsStorageConfiguration storage,
        PantsRuntimeConfiguration? runtime = null,
        PantsMemoryConfiguration? memory = null,
        PantsLeaseConfiguration? lease = null,
        PantsCompactionConfiguration? compaction = null,
        PantsRecoveryPolicy recoveryPolicy = PantsRecoveryPolicy.Strict,
        PantsCloudWritePolicy? cloudWritePolicy = null,
        IPantsClock? ttlClock = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        runtime ??= PantsRuntimeConfiguration.Default;
        memory ??= PantsMemoryConfiguration.Default;
        lease ??= PantsLeaseConfiguration.Default;
        var defaults = Configuration.Default(storage);
        return new PantsOpenOptions(defaults with
        {
            PerformanceGoal = runtime.PerformanceGoal,
            WorkloadProfile = runtime.WorkloadProfile,
            StorageTimeout = runtime.StorageTimeout,
            RuntimeResponseTimeout = runtime.RuntimeResponseTimeout,
            ShutdownTimeout = runtime.ShutdownTimeout,
            MemoryBudget = memory.Budget,
            BlockCachePolicy = memory.BlockCachePolicy,
            MemtableSizeLimitBytes = memory.MemtableSizeLimitBytes,
            MemtableFlushThresholdBytes = memory.MemtableFlushThresholdBytes,
            TransactionMemoryPoolBytes = memory.TransactionMemoryPoolBytes,
            WalBufferSizeBytes = memory.WalBufferSizeBytes,
            LeaseTimeToLive = lease.TimeToLive,
            LeaseClockSkewTolerance = lease.ClockSkewTolerance,
            MinimumEpoch = lease.MinimumEpoch,
            LeaseLossCallback = lease.LossCallback,
            Compaction = compaction,
            BackgroundCompaction = compaction?.BackgroundEnabled ?? defaults.BackgroundCompaction,
            RecoveryPolicy = recoveryPolicy,
            CloudWritePolicy = cloudWritePolicy ?? defaults.CloudWritePolicy,
            TtlClock = ttlClock ?? defaults.TtlClock
        });
    }

    public static PantsOpenOptions Local(string path) =>
        new(Configuration.Default(new PantsStorageConfiguration.Local(ValidatePath(path, nameof(path)))));

    public static PantsOpenOptions Cloud(
        string localCachePath,
        PantsCloudStorageLocation location) =>
        CloudMulti(localCachePath, PantsCloudStorageTopology.Shared(location));

    public static PantsOpenOptions CloudMulti(
        string localCachePath,
        PantsCloudStorageTopology topology) =>
        new(Configuration.Default(new PantsStorageConfiguration.Cloud(
            ValidatePath(localCachePath, nameof(localCachePath)),
            topology ?? throw new ArgumentNullException(nameof(topology)))));

    public static PantsOpenOptions SimulatedCloud(
        string localCachePath,
        string bucket,
        string prefix) =>
        new(Configuration.Default(new PantsStorageConfiguration.SimulatedCloud(
            ValidatePath(localCachePath, nameof(localCachePath)),
            ValidateNonEmpty(bucket, nameof(bucket)),
            prefix ?? throw new ArgumentNullException(nameof(prefix)))));

    public PantsOpenOptions WithPerformanceGoal(PantsPerformanceGoal goal) =>
        With(_configuration with { PerformanceGoal = goal });

    public PantsOpenOptions WithMemoryBudget(PantsMemoryBudget budget) =>
        With(_configuration with { MemoryBudget = budget });

    public PantsOpenOptions WithWorkloadProfile(PantsWorkloadProfile profile) =>
        With(_configuration with { WorkloadProfile = profile });

    public PantsOpenOptions WithRecoveryPolicy(PantsRecoveryPolicy policy) =>
        With(_configuration with { RecoveryPolicy = policy });

    public PantsOpenOptions WithBlockCachePolicy(PantsBlockCachePolicy policy) =>
        With(_configuration with { BlockCachePolicy = policy });

    public PantsOpenOptions WithCloudWritePolicy(PantsCloudWritePolicy policy) =>
        With(_configuration with
        {
            CloudWritePolicy = policy ?? throw new ArgumentNullException(nameof(policy))
        });

    public PantsOpenOptions WithStorageTimeout(TimeSpan timeout) =>
        With(_configuration with { StorageTimeout = timeout });

    public PantsOpenOptions WithRuntimeResponseTimeout(TimeSpan timeout) =>
        With(_configuration with { RuntimeResponseTimeout = timeout });

    public PantsOpenOptions WithShutdownTimeout(TimeSpan timeout) =>
        With(_configuration with { ShutdownTimeout = timeout });

    public PantsOpenOptions WithBackgroundCompaction(bool enabled) =>
        With(_configuration with
        {
            BackgroundCompaction = enabled,
            Compaction = _configuration.Compaction is null
                ? null
                : _configuration.Compaction with { BackgroundEnabled = enabled }
        });

    public PantsOpenOptions WithCompaction(PantsCompactionConfiguration configuration) =>
        With(_configuration with
        {
            Compaction = (configuration ?? throw new ArgumentNullException(nameof(configuration))) with
            {
                BackgroundEnabled = _configuration.BackgroundCompaction
            }
        });

    public PantsOpenOptions WithMemtableLimits(
        long sizeLimitBytes,
        long? flushThresholdBytes = null) =>
        With(_configuration with
        {
            MemtableSizeLimitBytes = sizeLimitBytes,
            MemtableFlushThresholdBytes = flushThresholdBytes ?? sizeLimitBytes
        });

    public PantsOpenOptions WithTransactionMemoryPool(long bytes) =>
        With(_configuration with { TransactionMemoryPoolBytes = bytes });

    public PantsOpenOptions WithWalBufferSize(int bytes) =>
        With(_configuration with { WalBufferSizeBytes = bytes });

    public PantsOpenOptions WithLeaseLossCallback(Action callback) =>
        With(_configuration with
        {
            LeaseLossCallback = callback ?? throw new ArgumentNullException(nameof(callback))
        });

    public PantsOpenOptions WithLeaseTimeToLive(TimeSpan timeToLive) =>
        With(_configuration with { LeaseTimeToLive = timeToLive });

    public PantsOpenOptions WithLeaseClockSkewTolerance(TimeSpan tolerance) =>
        With(_configuration with { LeaseClockSkewTolerance = tolerance });

    public PantsOpenOptions WithMinimumEpoch(ulong minimumEpoch) =>
        With(_configuration with { MinimumEpoch = minimumEpoch });

    public PantsOpenOptions WithTtlClock(IPantsClock clock) =>
        With(_configuration with
        {
            TtlClock = clock ?? throw new ArgumentNullException(nameof(clock))
        });

    public PantsOpenOptions WithSimulatedCloudLocalStorageBudget(long bytes)
    {
        if (Storage is not PantsStorageConfiguration.SimulatedCloud simulatedCloud)
        {
            throw PantsException.InvalidArgument(
                "A simulated-cloud budget requires simulated-cloud storage.");
        }

        return With(_configuration with
        {
            Storage = simulatedCloud with { LocalStorageBudgetBytes = bytes }
        });
    }

    internal PantsOpenOptions WithCoordinatorQueueCapacityForTesting(int capacity) =>
        With(_configuration with { CoordinatorQueueCapacity = capacity });

    internal PantsOpenOptions WithFlushAfterWalRecordsForTesting(int count) =>
        With(_configuration with { FlushAfterWalRecords = count });

    static PantsOpenOptions With(Configuration configuration) => new(configuration);

    void ValidateRawConfiguration()
    {
        ValidateEnum(Runtime.PerformanceGoal, nameof(Runtime.PerformanceGoal));
        ValidateEnum(Runtime.WorkloadProfile, nameof(Runtime.WorkloadProfile));
        ValidateEnum(RecoveryPolicy, nameof(RecoveryPolicy));
        ValidateEnum(Memory.BlockCachePolicy, nameof(Memory.BlockCachePolicy));

        if (Memory.Budget.Bytes is <= 0)
        {
            throw PantsException.InvalidArgument("Memory budget must be greater than zero.");
        }

        if (Memory.TransactionMemoryPoolBytes is <= 0)
        {
            throw PantsException.InvalidArgument("Transaction memory pool size must be greater than zero.");
        }

        if (Memory.Budget.Bytes is { } memoryBudgetBytes &&
            Memory.TransactionMemoryPoolBytes is { } transactionMemoryPoolBytes &&
            transactionMemoryPoolBytes > memoryBudgetBytes)
        {
            throw PantsException.ResourceLimit("Transaction memory pool exceeds the total memory budget.");
        }

        if (Memory.MemtableSizeLimitBytes is <= 0 || Memory.MemtableFlushThresholdBytes is <= 0)
        {
            throw PantsException.InvalidArgument("Memtable limits must be greater than zero.");
        }

        if (Memory.WalBufferSizeBytes is <= 0)
        {
            throw PantsException.InvalidArgument("WAL buffer size must be greater than zero.");
        }

        if (Memory.MemtableFlushThresholdBytes is { } flushThreshold &&
            Memory.MemtableSizeLimitBytes is { } sizeLimit &&
            flushThreshold > sizeLimit)
        {
            throw PantsException.InvalidArgument("Memtable flush threshold exceeds its size limit.");
        }

        if (Runtime.StorageTimeout < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument("Storage timeout must be at least one millisecond.");
        }

        if (Runtime.RuntimeResponseTimeout is { } explicitResponseTimeout &&
            (explicitResponseTimeout < TimeSpan.FromMilliseconds(1) ||
             explicitResponseTimeout <= Runtime.StorageTimeout))
        {
            throw PantsException.InvalidArgument(
                $"RuntimeResponseTimeout ({explicitResponseTimeout:c}) must be at least one " +
                $"millisecond and strictly greater than StorageTimeout ({Runtime.StorageTimeout:c}).");
        }

        if (Runtime.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Shutdown timeout must be greater than zero.");
        }

        if (Lease.TimeToLive < MinimumLeaseTimeToLive)
        {
            throw PantsException.InvalidArgument(
                $"LeaseTimeToLive ({Lease.TimeToLive:c}) must be at least " +
                $"{MinimumLeaseTimeToLive:c}.");
        }

        if (Lease.ClockSkewTolerance < TimeSpan.Zero ||
            Lease.ClockSkewTolerance >= Lease.TimeToLive)
        {
            throw PantsException.InvalidArgument(
                $"LeaseClockSkewTolerance ({Lease.ClockSkewTolerance:c}) must be non-negative " +
                $"and strictly less than LeaseTimeToLive ({Lease.TimeToLive:c}).");
        }

        if (CloudWritePolicy.EventualFlushSegmentGap <= 0 ||
            CloudWritePolicy.WalSealMinimumSegmentBytes <= 0 ||
            CloudWritePolicy.WalSealMaximumFlushDelay <= TimeSpan.Zero ||
            CloudWritePolicy.WalSealMaximumPendingWrites <= 0)
        {
            throw PantsException.InvalidArgument("Cloud write-policy limits must be greater than zero.");
        }

        if (Storage is PantsStorageConfiguration.SimulatedCloud { LocalStorageBudgetBytes: <= 0 })
        {
            throw PantsException.InvalidArgument(
                "Simulated-cloud local storage budget must be greater than zero.");
        }

        if (_configuration.CoordinatorQueueCapacity <= 0 || _configuration.FlushAfterWalRecords < 0)
        {
            throw PantsException.InvalidArgument("Internal runtime limits are invalid.");
        }

        if (Compaction.L0SizeTriggerBytes <= 0 || Compaction.L0FileCountTrigger <= 0 ||
            Compaction.MaximumInputFiles <= 0 || Compaction.LevelMultiplier <= 1 ||
            Compaction.L1TargetSizeBytes <= 0 || Compaction.MaximumLevels < 2 ||
            Compaction.TargetSstSizeBytes is <= 0)
        {
            throw PantsException.InvalidArgument("Compaction limits are invalid.");
        }

        ValidateStorage(Storage);
    }

    static void ValidateStorage(PantsStorageConfiguration storage)
    {
        switch (storage)
        {
            case PantsStorageConfiguration.InMemory:
            case PantsStorageConfiguration.Local:
            case PantsStorageConfiguration.SimulatedCloud:
                return;
            case PantsStorageConfiguration.Cloud cloud:
                ValidateCloudLocation(cloud.Topology.Wal, "WAL");
                ValidateCloudLocation(cloud.Topology.Sst, "SST");
                ValidateCloudLocation(cloud.Topology.Control, "control");
                return;
            default:
                throw PantsException.InvalidArgument("The storage configuration is invalid.");
        }
    }

    static void ValidateCloudLocation(PantsCloudStorageLocation? location, string objectClass)
    {
        if (location?.Provider is null)
        {
            throw PantsException.InvalidArgument($"The {objectClass} cloud location is incomplete.");
        }

        if (location.Prefix is null || location.Prefix.StartsWith('/'))
        {
            throw PantsException.InvalidArgument(
                $"The {objectClass} cloud prefix must be relative and non-null.");
        }

        var report = location.Validate();
        if (!report.IsValid)
        {
            throw PantsException.InvalidArgument(
                $"The {objectClass} cloud location is invalid: {report.Findings[0].Message}");
        }
    }

    static void ValidateEnum<TEnum>(TEnum value, string description)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw PantsException.InvalidArgument($"The {description} value is invalid.");
        }
    }

    static string ValidatePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    static string ValidateNonEmpty(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    sealed record Configuration(
        PantsStorageConfiguration Storage,
        PantsPerformanceGoal PerformanceGoal,
        PantsMemoryBudget MemoryBudget,
        PantsWorkloadProfile WorkloadProfile,
        PantsRecoveryPolicy RecoveryPolicy,
        PantsBlockCachePolicy BlockCachePolicy,
        PantsCloudWritePolicy CloudWritePolicy,
        TimeSpan StorageTimeout,
        TimeSpan? RuntimeResponseTimeout,
        TimeSpan ShutdownTimeout,
        bool BackgroundCompaction,
        long? MemtableSizeLimitBytes,
        long? MemtableFlushThresholdBytes,
        long? TransactionMemoryPoolBytes,
        int? WalBufferSizeBytes,
        TimeSpan LeaseTimeToLive,
        TimeSpan LeaseClockSkewTolerance,
        Action? LeaseLossCallback,
        IPantsClock TtlClock,
        PantsCompactionConfiguration? Compaction,
        int CoordinatorQueueCapacity,
        int FlushAfterWalRecords,
        ulong MinimumEpoch)
    {
        public static Configuration Default(PantsStorageConfiguration storage) => new(
            storage,
            PantsPerformanceGoal.Latency,
            PantsMemoryBudget.Auto,
            PantsWorkloadProfile.Mixed,
            PantsRecoveryPolicy.Strict,
            PantsBlockCachePolicy.Lru,
            new PantsCloudWritePolicy(),
            TimeSpan.FromSeconds(30),
            null,
            TimeSpan.FromSeconds(30),
            true,
            null,
            null,
            null,
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(15),
            null,
            SystemPantsClock.Instance,
            null,
            128,
            0,
            0);
    }
}
