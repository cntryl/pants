namespace Cntryl.Pants;

/// <summary>Immutable and fully validated database-open configuration.</summary>
public sealed class PantsOpenOptions
{
    const long FallbackMemoryBudgetBytes = 512L * 1024 * 1024;
    readonly Configuration _configuration;

    PantsOpenOptions(Configuration configuration)
    {
        _configuration = configuration;
        MemoryBudgetBytes = ResolveMemoryBudget(configuration.MemoryBudget);
        TransactionMemoryPoolBytes = configuration.TransactionMemoryPoolBytes ??
                                     Math.Max(1, MemoryBudgetBytes / 10);
        // Mirrors Midge's derive_memory_pools: a bounded pool for compaction's streaming merge
        // buffers, normally capped like the transaction pool (roughly a tenth of the budget)
        // and at 256 MiB. Sub-MiB automatically partitioned budgets instead prioritize enough
        // room for the decoded input blocks required to make compaction progress; the derived
        // memtables shrink with the same total budget, so this does not oversubscribe the limit.
        var remainingAfterRequiredPools = Math.Max(
            0,
            MemoryBudgetBytes - TransactionMemoryPoolBytes - 2);
        var desiredCompactionPoolBytes = MemoryBudgetBytes < 1024 * 1024 &&
                                         configuration.MemtableSizeLimitBytes is null
            ? Math.Max(1, MemoryBudgetBytes * 2 / 3)
            : Math.Max(1, Math.Min(MemoryBudgetBytes / 10, 256L * 1024 * 1024));
        var desiredScanPoolBytes = Math.Max(
            1,
            Math.Min(MemoryBudgetBytes / 20, 128L * 1024 * 1024));
        if (configuration.MemtableSizeLimitBytes is > 0 and <= long.MaxValue / 2)
        {
            var unallocatedBytes = MemoryBudgetBytes -
                                   TransactionMemoryPoolBytes -
                                   2 * configuration.MemtableSizeLimitBytes.Value -
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
        var baseMemtable = configuration.PerformanceGoal switch
        {
            PantsPerformanceGoal.Latency => 64L * 1024 * 1024,
            PantsPerformanceGoal.Throughput => 256L * 1024 * 1024,
            PantsPerformanceGoal.Economy => 32L * 1024 * 1024,
            _ => throw PantsException.InvalidArgument("Unknown performance goal.")
        };
        var desiredMemtable = configuration.WorkloadProfile switch
        {
            PantsWorkloadProfile.WriteHeavy => baseMemtable * 2,
            PantsWorkloadProfile.ReadMostly => baseMemtable / 2,
            _ => baseMemtable
        };
        MemtableSizeLimitBytes = configuration.MemtableSizeLimitBytes ??
                                 Math.Max(1, Math.Min(desiredMemtable, maximumMemtable));
        MemtableFlushThresholdBytes = configuration.MemtableFlushThresholdBytes ??
                                      MemtableSizeLimitBytes;
        BlockCacheBytes = Math.Max(
            0,
            MemoryBudgetBytes -
            TransactionMemoryPoolBytes -
            CompactionMemoryPoolBytes -
            ScanMemoryPoolBytes -
            2 * MemtableSizeLimitBytes);
        if (configuration.PerformanceGoal == PantsPerformanceGoal.Economy)
        {
            BlockCacheBytes = Math.Min(BlockCacheBytes, 256L * 1024 * 1024);
        }

        BlockSizeBytes = (configuration.PerformanceGoal, configuration.WorkloadProfile) switch
        {
            (PantsPerformanceGoal.Latency, _) => 16 * 1024,
            (PantsPerformanceGoal.Economy, _) => 32 * 1024,
            (PantsPerformanceGoal.Throughput, PantsWorkloadProfile.RangeScan) => 128 * 1024,
            _ => 64 * 1024
        };
        TargetSstSizeBytes = configuration.PerformanceGoal switch
        {
            PantsPerformanceGoal.Latency => 128L * 1024 * 1024,
            PantsPerformanceGoal.Throughput => 512L * 1024 * 1024,
            _ => 256L * 1024 * 1024
        };
        WalBufferSizeBytes = configuration.WalBufferSizeBytes ??
                             checked((int)Math.Clamp(
                                 configuration.PerformanceGoal switch
                                 {
                                     PantsPerformanceGoal.Latency => 128L * 1024,
                                     PantsPerformanceGoal.Throughput => 1024L * 1024,
                                     _ => 256L * 1024
                                 },
                                 1,
                                 MemoryBudgetBytes));
        L0CompactionTrigger = (configuration.PerformanceGoal, configuration.WorkloadProfile) switch
        {
            (PantsPerformanceGoal.Latency, _) => 3,
            (_, PantsWorkloadProfile.WriteHeavy) => 8,
            (PantsPerformanceGoal.Throughput, _) => 6,
            _ => 4
        };
        Compaction = configuration.Compaction ?? new PantsCompactionConfiguration(
            L0FileCountTrigger: L0CompactionTrigger);
        TargetSstSizeBytes = Compaction.TargetSstSizeBytes ?? TargetSstSizeBytes;

        Validate();
    }

    public PantsStorageConfiguration Storage => _configuration.Storage;

    public PantsPerformanceGoal PerformanceGoal => _configuration.PerformanceGoal;

    public PantsMemoryBudget MemoryBudget => _configuration.MemoryBudget;

    public long MemoryBudgetBytes { get; }

    /// <summary>
    /// Bytes reserved for compaction's streaming merge/partition buffers (see
    /// <c>ResourceBudget</c>), derived from <see cref="MemoryBudgetBytes"/> the same way the
    /// transaction pool is. Not separately overridable today (no corresponding configuration
    /// knob) — always derived.
    /// </summary>
    public long CompactionMemoryPoolBytes { get; }

    /// <summary>
    /// Bytes reserved for the decoded blocks held by active SST scan cursors. The pool is shared
    /// by all scans in the database and is derived from <see cref="MemoryBudgetBytes"/>.
    /// </summary>
    public long ScanMemoryPoolBytes { get; }

    public PantsWorkloadProfile WorkloadProfile => _configuration.WorkloadProfile;

    public PantsRecoveryPolicy RecoveryPolicy => _configuration.RecoveryPolicy;

    public PantsBlockCachePolicy BlockCachePolicy => _configuration.BlockCachePolicy;

    public PantsCloudWritePolicy CloudWritePolicy => _configuration.CloudWritePolicy;

    public TimeSpan StorageTimeout => _configuration.StorageTimeout;

    /// <summary>
    /// Maximum time a caller waits for an admitted runtime command to publish its response.
    /// A timeout does not cancel accepted work and therefore leaves its outcome unknown.
    /// </summary>
    public TimeSpan RuntimeResponseTimeout =>
        _configuration.RuntimeResponseTimeout ?? DeriveRuntimeResponseTimeout(StorageTimeout);

    public TimeSpan ShutdownTimeout => _configuration.ShutdownTimeout;

    public bool BackgroundCompaction => _configuration.BackgroundCompaction;

    public long MemtableSizeLimitBytes { get; }

    public long MemtableFlushThresholdBytes { get; }

    public long TransactionMemoryPoolBytes { get; }

    public long BlockCacheBytes { get; }

    public int BlockSizeBytes { get; }

    public long TargetSstSizeBytes { get; }

    public int WalBufferSizeBytes { get; }

    public int L0CompactionTrigger { get; }

    public PantsCompactionConfiguration Compaction { get; }

    public TimeSpan LeaseClockSkewTolerance => _configuration.LeaseClockSkewTolerance;

    public ulong MinimumEpoch => _configuration.MinimumEpoch;

    public Action? LeaseLossCallback => _configuration.LeaseLossCallback;

    public IPantsClock TtlClock => _configuration.TtlClock;

    internal int CoordinatorQueueCapacity => _configuration.CoordinatorQueueCapacity;

    internal int FlushAfterWalRecords => _configuration.FlushAfterWalRecords;

    public static PantsOpenOptions InMemory() =>
        new(Configuration.Default(new PantsStorageConfiguration.InMemory()));

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

    internal static PantsOpenOptions FromSettings(
        PantsStorageConfiguration storage,
        PantsPerformanceGoal performanceGoal,
        PantsMemoryBudget memoryBudget,
        PantsWorkloadProfile workloadProfile,
        PantsRecoveryPolicy recoveryPolicy,
        PantsBlockCachePolicy blockCachePolicy,
        PantsCloudWritePolicy? cloudWritePolicy,
        TimeSpan storageTimeout,
        TimeSpan? runtimeResponseTimeout,
        TimeSpan shutdownTimeout,
        bool backgroundCompaction,
        long? memtableSizeLimitBytes,
        long? memtableFlushThresholdBytes,
        long? transactionMemoryPoolBytes,
        int? walBufferSizeBytes,
        TimeSpan leaseClockSkewTolerance,
        PantsCompactionConfiguration? compaction,
        ulong minimumEpoch)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var defaults = Configuration.Default(storage);
        return new PantsOpenOptions(defaults with
        {
            PerformanceGoal = performanceGoal,
            MemoryBudget = memoryBudget,
            WorkloadProfile = workloadProfile,
            RecoveryPolicy = recoveryPolicy,
            BlockCachePolicy = blockCachePolicy,
            CloudWritePolicy = cloudWritePolicy ?? defaults.CloudWritePolicy,
            StorageTimeout = storageTimeout,
            RuntimeResponseTimeout = runtimeResponseTimeout,
            ShutdownTimeout = shutdownTimeout,
            BackgroundCompaction = backgroundCompaction,
            MemtableSizeLimitBytes = memtableSizeLimitBytes,
            MemtableFlushThresholdBytes = memtableFlushThresholdBytes,
            TransactionMemoryPoolBytes = transactionMemoryPoolBytes,
            WalBufferSizeBytes = walBufferSizeBytes,
            LeaseClockSkewTolerance = leaseClockSkewTolerance,
            Compaction = compaction,
            MinimumEpoch = minimumEpoch
        });
    }

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
        With(_configuration with { BackgroundCompaction = enabled });

    public PantsOpenOptions WithCompaction(PantsCompactionConfiguration configuration) =>
        With(_configuration with
        {
            Compaction = configuration ?? throw new ArgumentNullException(nameof(configuration))
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

    void Validate()
    {
        ValidateEnum(PerformanceGoal, nameof(PerformanceGoal));
        ValidateEnum(WorkloadProfile, nameof(WorkloadProfile));
        ValidateEnum(RecoveryPolicy, nameof(RecoveryPolicy));
        ValidateEnum(BlockCachePolicy, nameof(BlockCachePolicy));

        if (TransactionMemoryPoolBytes <= 0)
        {
            throw PantsException.InvalidArgument("Transaction memory pool size must be greater than zero.");
        }

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

        if (StorageTimeout < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument("Storage timeout must be at least one millisecond.");
        }

        if (_configuration.RuntimeResponseTimeout is { } explicitResponseTimeout &&
            (explicitResponseTimeout < TimeSpan.FromMilliseconds(1) ||
             explicitResponseTimeout <= StorageTimeout))
        {
            throw PantsException.InvalidArgument(
                $"RuntimeResponseTimeout ({explicitResponseTimeout:c}) must be at least one " +
                $"millisecond and strictly greater than StorageTimeout ({StorageTimeout:c}).");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Shutdown timeout must be greater than zero.");
        }

        if (LeaseClockSkewTolerance < TimeSpan.Zero ||
            LeaseClockSkewTolerance > TimeSpan.FromSeconds(30))
        {
            throw PantsException.InvalidArgument(
                "Lease clock-skew tolerance must be between zero and 30 seconds.");
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

        switch (location.Provider)
        {
            case PantsCloudProviderConfiguration.AwsS3 aws:
                ValidateProviderText(aws.Bucket, "AWS S3 bucket");
                ValidateProviderText(aws.Region, "AWS region");
                ArgumentNullException.ThrowIfNull(aws.Credentials);
                break;
            case PantsCloudProviderConfiguration.S3Compatible compatible:
                ValidateProviderText(compatible.Bucket, "S3-compatible bucket");
                ValidateProviderText(compatible.Region, "S3-compatible region");
                ValidateHttpEndpoint(compatible.Endpoint, "S3-compatible endpoint");
                ArgumentNullException.ThrowIfNull(compatible.Credentials);
                break;
            case PantsCloudProviderConfiguration.AzureBlob azure:
                ValidateProviderText(azure.Account, "Azure account");
                ValidateProviderText(azure.Container, "Azure container");
                ValidateOptionalHttpEndpoint(azure.Endpoint, "Azure endpoint");
                ArgumentNullException.ThrowIfNull(azure.Credential);
                break;
            case PantsCloudProviderConfiguration.Gcs gcs:
                ValidateProviderText(gcs.Bucket, "GCS bucket");
                ValidateProviderText(gcs.ProjectId, "GCS project ID");
                ValidateOptionalHttpEndpoint(gcs.Endpoint, "GCS endpoint");
                ValidateEnum(gcs.ApiStyle, nameof(gcs.ApiStyle));
                ArgumentNullException.ThrowIfNull(gcs.Credential);
                break;
            default:
                throw PantsException.InvalidArgument(
                    $"The {objectClass} cloud provider is unsupported.");
        }
    }

    static void ValidateProviderText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw PantsException.InvalidArgument($"The {description} must not be empty.");
        }
    }

    static void ValidateOptionalHttpEndpoint(Uri? endpoint, string description)
    {
        if (endpoint is not null)
        {
            ValidateHttpEndpoint(endpoint, description);
        }
    }

    static void ValidateHttpEndpoint(Uri? endpoint, string description)
    {
        if (endpoint is null ||
            !endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw PantsException.InvalidArgument(
                $"The {description} must be an absolute HTTP or HTTPS URI.");
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

    static long ResolveMemoryBudget(PantsMemoryBudget budget)
    {
        if (budget.Bytes is { } bytes)
        {
            return bytes;
        }

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? Math.Max(5, available / 2) : FallbackMemoryBudgetBytes;
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

    static TimeSpan DeriveRuntimeResponseTimeout(TimeSpan storageTimeout)
    {
        const long marginTicks = TimeSpan.TicksPerSecond * 30;
        var derivedTicks = storageTimeout.Ticks > TimeSpan.MaxValue.Ticks - marginTicks
            ? TimeSpan.MaxValue.Ticks
            : storageTimeout.Ticks + marginTicks;
        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(60).Ticks, derivedTicks));
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
            TimeSpan.FromSeconds(15),
            null,
            SystemPantsClock.Instance,
            null,
            128,
            0,
            0);
    }
}
