using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Cntryl.Pants.Runtime.Internal;

sealed class Actor : IAsyncDisposable
{
    static readonly TimeSpan ProviderStartupCleanupTimeout = TimeSpan.FromSeconds(1);
    static readonly TimeSpan InitialCloudWalSealRetryDelay = TimeSpan.FromMilliseconds(25);
    static readonly TimeSpan MaximumCloudWalSealRetryDelay = TimeSpan.FromSeconds(1);
    readonly Lock _cloudWalSealDeadlineGate = new();
    readonly HashSet<Task> _cloudWalSealDeadlineTasks = [];
    readonly Lock _directReadAdmissionGate = new();
    readonly CancellationTokenSource _loopCancellation = new();

    readonly ConcurrentDictionary<ColumnFamilyIdentity, byte> _writeStallHints =
        new(ColumnFamilyIdentityComparer.Instance);

    int _activeCompactionRequests;
    bool _backgroundCompactionEnabled;
    bool _backgroundCompactionPending;
    readonly CloudCompactionOutputPublisher? _cloudCompactionOutputPublisher;
    readonly CloudDdlCoordinator? _cloudDdlCoordinator;
    readonly CloudFlushRetryScheduler _cloudFlushRetries;
    readonly CloudLeaseCoordinator? _cloudLease;
    readonly CancellationTokenSource? _cloudLeaseCancellation;
    readonly Task? _cloudLeaseHeartbeat;
    CloudWorkScheduler _cloudMaintenanceScheduler = null!;
    CloudMemtableSegmentTracker? _cloudMemtableSegments;
    readonly bool _cloudMode;
    readonly ICloudPersistence? _cloudPersistence;
    CloudWorkScheduler _cloudWalDrainScheduler = null!;
    CloudWalSealController? _cloudWalSealController;
    CancellationTokenSource? _cloudWalSealDeadlineCancellation;
    bool _cloudWalSealPending;
    readonly CloudWalUploadTracker _cloudWalUploads;
    RuntimeWorker _cloudWorker = null!;
    Channel<IRuntimeCommand> _commands = null!;
    long _commandsEnqueued;
    CompactionRuntimeService _compactionRuntime = null!;
    DatabaseVersion _currentVersion = null!;
    int _deferredCompactionScheduled;
    readonly LocalDiskStore? _diskStore;
    int _disposed;
    readonly IFailpointHandler _failpoints;
    FlushRuntimeService _flushRuntime = null!;
    bool _garbageCollectionPending;
    RuntimeWorker _garbageCollectionWorker = null!;
    HybridCacheManager? _hybridCache;
    ImmutableFlushPipeline _immutableFlushPipeline = null!;
    Task _loopTask = null!;
    RuntimeWorker _manifestWorker = null!;
    long _nextRuntimeRequestId;
    long _nextVerificationBarrierToken;
    readonly RuntimePlan _options;
    int _persistenceAnomaly;
    PantsRuntimeMetrics _publishedRuntimeMetrics = new();
    int _queuedCommands;
    bool _readAmplificationCompactionPending;
    bool _recoveredMemtableFlushPending;
    RuntimeMetricsSnapshotFactory _runtimeMetricsSnapshotFactory = null!;
    readonly RuntimeResponseRegistry _runtimeResponses;
    readonly TimeProvider _runtimeTimeProvider;
    readonly ResourceBudget _scanMemoryBudget;
    bool _shutdownPreparationCompleted;
    bool _shutdownRequested;
    int _snapshotReleaseCollectionRequired;

    readonly RuntimeState _state;
    readonly StorageVerificationDelegate _storageVerifier;
    readonly RuntimeTelemetry _telemetry;
    OnlineVerificationBarrier? _verificationBarrier;
    readonly VerificationBarrierResponseDelegate _verificationBarrierResponse;
    TaskCompletionSource? _verificationMaintenanceCompletion;
    long _walCloudDurableSequence;
    readonly WalMetricsRecorder _walMetrics;
    WalRuntimeService _walRuntime = null!;
    bool _workersStarted;

    /// <summary>
    ///     Assigns every dependency resolved synchronously or asynchronously by
    ///     <see cref="OpenAsync" /> before any storage backend I/O begins. The remaining fields are
    ///     populated by <see cref="FinishStartupAsync" />, which runs recovery and worker startup as
    ///     genuine instance methods (for example <see cref="EnsureCloudWriteAuthorityValid" /> and
    ///     <see cref="DrainCloudWalBacklogAsync(CancellationToken)" />) against this already-constructed
    ///     instance.
    /// </summary>
    Actor(
        RuntimePlan options,
        bool backgroundCompactionEnabled,
        RuntimeTelemetry telemetry,
        WalMetricsRecorder walMetrics,
        CloudWalUploadTracker cloudWalUploads,
        CloudFlushRetryScheduler cloudFlushRetries,
        StorageVerificationDelegate storageVerifier,
        VerificationBarrierResponseDelegate verificationBarrierResponse,
        IFailpointHandler failpoints,
        TimeProvider runtimeTimeProvider,
        RuntimeResponseRegistry runtimeResponses,
        ResourceBudget scanMemoryBudget,
        RuntimeState state,
        bool cloudMode,
        LocalDiskStore? diskStore,
        ICloudPersistence? cloudPersistence,
        CloudCompactionOutputPublisher? cloudCompactionOutputPublisher,
        CloudDdlCoordinator? cloudDdlCoordinator,
        CloudLeaseCoordinator? cloudLease,
        CancellationTokenSource? cloudLeaseCancellation,
        Task? cloudLeaseHeartbeat,
        long walCloudDurableSequence)
    {
        _options = options;
        _backgroundCompactionEnabled = backgroundCompactionEnabled;
        _telemetry = telemetry;
        _walMetrics = walMetrics;
        _cloudWalUploads = cloudWalUploads;
        _cloudFlushRetries = cloudFlushRetries;
        _storageVerifier = storageVerifier;
        _verificationBarrierResponse = verificationBarrierResponse;
        _failpoints = failpoints;
        _runtimeTimeProvider = runtimeTimeProvider;
        _runtimeResponses = runtimeResponses;
        _scanMemoryBudget = scanMemoryBudget;
        _state = state;
        _cloudMode = cloudMode;
        _diskStore = diskStore;
        _cloudPersistence = cloudPersistence;
        _cloudCompactionOutputPublisher = cloudCompactionOutputPublisher;
        _cloudDdlCoordinator = cloudDdlCoordinator;
        _cloudLease = cloudLease;
        _cloudLeaseCancellation = cloudLeaseCancellation;
        _cloudLeaseHeartbeat = cloudLeaseHeartbeat;
        _walCloudDurableSequence = walCloudDurableSequence;
    }

    public bool IsPrimaryLeaseHealthy =>
        (_diskStore?.IsLeaseHealthy ?? true) && (_cloudLease?.IsHealthy ?? true);

    internal long CommandsEnqueued => Volatile.Read(ref _commandsEnqueued);

    internal bool IsDiskStoreDisposed => _diskStore?.IsDisposed ?? true;

    internal bool AreOwnedRuntimeResourcesDisposed =>
        _cloudFlushRetries.IsDisposed &&
        _cloudWalDrainScheduler.IsDisposed &&
        _cloudMaintenanceScheduler.IsDisposed &&
        _cloudWorker.IsDisposed &&
        _walRuntime.IsDisposed &&
        _flushRuntime.IsDisposed &&
        _compactionRuntime.IsDisposed &&
        _manifestWorker.IsDisposed &&
        _garbageCollectionWorker.IsDisposed &&
        IsDiskStoreDisposed;

    bool UsesBackgroundImmutableFlushes =>
        _diskStore is not null && _options.Storage is PantsStorageConfiguration.Local;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? cleanupFailure = null;
        Exception? loopFailure = null;

        async ValueTask CaptureCleanupAsync(Func<ValueTask> cleanup)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        void CaptureCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        var cloudWalSealDeadlineTasks = CancelCloudWalSealDeadlinesForDisposal();
        if (cloudWalSealDeadlineTasks.Length != 0)
        {
            try
            {
                await Task.WhenAll(cloudWalSealDeadlineTasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Volatile.Write(ref _persistenceAnomaly, 1);
            }
        }

        await CaptureCleanupAsync(_cloudFlushRetries.DisposeAsync).ConfigureAwait(false);
        _commands.Writer.TryComplete();
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            loopFailure = exception;
        }

        await CaptureCleanupAsync(_cloudWalDrainScheduler.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_cloudMaintenanceScheduler.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_cloudWorker.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_walRuntime.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_flushRuntime.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_compactionRuntime.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_manifestWorker.DisposeAsync).ConfigureAwait(false);
        await CaptureCleanupAsync(_garbageCollectionWorker.DisposeAsync).ConfigureAwait(false);
        if (_cloudLeaseCancellation is not null)
        {
            await CaptureCleanupAsync(() => new ValueTask(_cloudLeaseCancellation.CancelAsync()))
                .ConfigureAwait(false);
        }

        if (_cloudLeaseHeartbeat is not null)
        {
            await CaptureCleanupAsync(() => new ValueTask(_cloudLeaseHeartbeat))
                .ConfigureAwait(false);
        }

        if (_cloudLease is not null)
        {
            try
            {
                await _cloudLease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (PantsException)
            {
                // A failed release leaves the bounded lease to expire naturally.
            }
        }

        CaptureCleanup(() => _cloudLeaseCancellation?.Dispose());
        CaptureCleanup(() => _cloudLease?.Dispose());
        if (_cloudPersistence is not null)
        {
            await CaptureCleanupAsync(_cloudPersistence.DisposeAsync).ConfigureAwait(false);
        }

        CaptureCleanup(() => _hybridCache?.Dispose());
        CaptureCleanup(() => _diskStore?.Dispose());
        CaptureCleanup(_loopCancellation.Dispose);

        var failure = loopFailure ?? cleanupFailure;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static async ValueTask<Actor> OpenAsync(
        RuntimePlan options,
        IPantsClock ttlClock,
        RuntimeTelemetry telemetry,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backgroundCompactionEnabled = options.BackgroundCompaction;
        var walMetrics = new WalMetricsRecorder(telemetry);
        var cloudWalUploads = new CloudWalUploadTracker(telemetry);
        var cloudFlushRetries = new CloudFlushRetryScheduler(telemetry);
        var storageVerifier = dependencies.StorageVerifier;
        var verificationBarrierResponse = dependencies.VerificationBarrierResponse;
        var failpoints = dependencies.Failpoints;
        var runtimeTimeProvider = dependencies.RuntimeTimeProvider;
        var runtimeResponses = new RuntimeResponseRegistry(telemetry, runtimeTimeProvider);
        var scanMemoryBudget = new ResourceBudget(options.ScanMemoryPoolBytes);
        var leaseClock = new MonotonicPantsClock(dependencies.LeaseClock);
        var leaseHeartbeatInterval = dependencies.LeaseHeartbeatInterval ??
                                     options.LeaseHeartbeatInterval;
        var state = new RuntimeState(ttlClock, telemetry);
        var startupPhases = dependencies.StartupPhases;

        var cloudMode = false;
        LocalDiskStore? diskStore = null;
        ICloudPersistence? cloudPersistence = null;
        CloudCompactionOutputPublisher? cloudCompactionOutputPublisher = null;
        CloudDdlCoordinator? cloudDdlCoordinator = null;
        CloudLeaseCoordinator? cloudLease = null;
        CancellationTokenSource? cloudLeaseCancellation = null;
        Task? cloudLeaseHeartbeat = null;
        var walCloudDurableSequence = 0L;

        switch (options.Storage)
        {
            case PantsStorageConfiguration.InMemory:
                cloudMode = false;
                break;
            case PantsStorageConfiguration.Local local:
                diskStore = LocalDiskStore.Open(
                    local.Path,
                    state,
                    options.MinimumEpoch,
                    options.RecoveryPolicy,
                    options.PerformanceGoal,
                    options.LeaseClockSkewTolerance,
                    options.LeaseLossCallback,
                    dependencies.Failpoints,
                    options.Compaction,
                    options.TargetSstSizeBytes,
                    options.BlockCachePolicy,
                    options.BlockCacheBytes,
                    leaseHeartbeatInterval,
                    startupPhases: startupPhases,
                    leaseClock: leaseClock,
                    leaseTimeToLive: options.LeaseTimeToLive);
                cloudMode = false;
                break;
            case PantsStorageConfiguration.SimulatedCloud simulated:
                SimulatedCloudHydrationResult simulatedHydration;
                using (startupPhases.Measure(StartupPhase.CloudControlHydration))
                {
                    simulatedHydration = SimulatedCloudPersistence.PrepareLocalCache(
                        simulated.LocalCachePath,
                        options.RecoveryPolicy);
                }

                if (simulatedHydration.RequiresSalvage)
                {
                    state.MarkSalvageMode();
                }

                try
                {
                    diskStore = LocalDiskStore.Open(
                        simulated.LocalCachePath,
                        state,
                        simulatedHydration.MinimumWriterEpoch,
                        options.RecoveryPolicy,
                        options.PerformanceGoal,
                        options.LeaseClockSkewTolerance,
                        options.LeaseLossCallback,
                        dependencies.Failpoints,
                        options.Compaction,
                        options.TargetSstSizeBytes,
                        options.BlockCachePolicy,
                        options.BlockCacheBytes,
                        leaseHeartbeatInterval,
                        new SimulatedCloudSstSourceFactory(simulated.LocalCachePath),
                        startupPhases,
                        leaseClock,
                        options.LeaseTimeToLive);
                    var simulatedPersistence = new SimulatedCloudPersistence(
                        simulated.LocalCachePath,
                        diskStore.WriterEpoch,
                        dependencies.Failpoints);
                    cloudPersistence = simulatedPersistence;
                    cloudCompactionOutputPublisher = new SimulatedCloudCompactionPublisher(
                        simulated.LocalCachePath,
                        dependencies.Failpoints).PublishAsync;
                    cloudDdlCoordinator = new CloudDdlCoordinator(
                        simulated.LocalCachePath,
                        simulatedPersistence,
                        diskStore,
                        dependencies.Failpoints);
                    using (startupPhases.Measure(StartupPhase.IntentReconciliation))
                    {
                        await cloudDdlCoordinator
                            .ReconcileStartupAsync(state, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    cloudMode = true;
                }
                catch
                {
                    CleanupFailedDiskStartup(diskStore);
                    throw;
                }

                break;
            case PantsStorageConfiguration.Cloud cloud:
                var cloudStartupDeadline = OperationDeadline.FromBudget(
                    options.RuntimeResponseTimeout,
                    runtimeTimeProvider);
                var objectStores = await ProviderObjectStoreSet.OpenAsync(
                    cloud.Topology,
                    options.StorageTimeout,
                    dependencies.CloudHttpClient,
                    dependencies.RuntimeTimeProvider,
                    cancellationToken).ConfigureAwait(false);
                ProviderCloudPersistence? providerPersistence = null;
                try
                {
                    cloudLease = new CloudLeaseCoordinator(
                        new CloudObjectLeaseStore(
                            objectStores.Control,
                            PantsCloudObjectLayout.LeaseObjectKey),
                        leaseClock,
                        $"pants-{Environment.ProcessId}-{Guid.NewGuid():N}",
                        options.LeaseTimeToLive,
                        options.LeaseClockSkewTolerance,
                        options.LeaseLossCallback);
                    ulong cloudEpoch;
                    using (startupPhases.Measure(StartupPhase.Lease))
                    {
                        cloudEpoch = await cloudStartupDeadline.RunMutationAsync(
                                cloudLease.AcquireAsync,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    ProviderCloudHydrationResult hydration;
                    using (startupPhases.Measure(StartupPhase.CloudControlHydration))
                    {
                        hydration = await cloudStartupDeadline.RunAsync(
                                token => ProviderCloudPersistence.HydrateLocalCacheAsync(
                                    cloud.LocalCachePath,
                                    objectStores.Wal,
                                    objectStores.Sst,
                                    objectStores.Control,
                                    options.RecoveryPolicy,
                                    token),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (hydration.RequiresSalvage)
                    {
                        state.MarkSalvageMode();
                    }

                    diskStore = LocalDiskStore.Open(
                        cloud.LocalCachePath,
                        state,
                        cloudEpoch - 1,
                        options.RecoveryPolicy,
                        options.PerformanceGoal,
                        options.LeaseClockSkewTolerance,
                        options.LeaseLossCallback,
                        dependencies.Failpoints,
                        options.Compaction,
                        options.TargetSstSizeBytes,
                        options.BlockCachePolicy,
                        options.BlockCacheBytes,
                        leaseHeartbeatInterval,
                        new ProviderCloudSstSourceFactory(objectStores.Sst),
                        startupPhases,
                        leaseClock,
                        options.LeaseTimeToLive);
                    providerPersistence = new ProviderCloudPersistence(
                        cloud.LocalCachePath,
                        objectStores.Wal,
                        objectStores.Sst,
                        objectStores.Control,
                        cloudLease,
                        dependencies.Failpoints);
                    cloudPersistence = providerPersistence;
                    cloudCompactionOutputPublisher = new ProviderCloudCompactionPublisher(
                        cloud.LocalCachePath,
                        objectStores.Sst,
                        objectStores.Control,
                        cloudLease,
                        dependencies.Failpoints).PublishAsync;
                    await cloudStartupDeadline.RunMutationAsync(
                            providerPersistence.FenceWalCatalogAsync,
                            cancellationToken)
                        .ConfigureAwait(false);
                    cloudDdlCoordinator = new CloudDdlCoordinator(
                        cloud.LocalCachePath,
                        providerPersistence,
                        diskStore,
                        dependencies.Failpoints);
                    using (startupPhases.Measure(StartupPhase.IntentReconciliation))
                    {
                        await cloudStartupDeadline.RunMutationAsync(
                                token => cloudDdlCoordinator.ReconcileStartupAsync(state, token),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    walCloudDurableSequence = checked((long)hydration.CloudDurableSequence);
                    cloudLeaseCancellation = new CancellationTokenSource();
                    cloudLeaseHeartbeat = RunCloudLeaseHeartbeatAsync(
                        cloudLease,
                        leaseHeartbeatInterval,
                        cloudLeaseCancellation.Token);
                    cloudMode = true;
                }
                catch
                {
                    if (cloudLease is not null)
                    {
                        await CleanupFailedProviderStartupAsync(
                            cloudLease,
                            diskStore,
                            cloudLeaseCancellation,
                            cloudLeaseHeartbeat).ConfigureAwait(false);
                    }

                    await DisposeFailedCloudStartupResourcesAsync(
                        providerPersistence,
                        objectStores).ConfigureAwait(false);

                    throw;
                }

                break;
            default:
                throw PantsException.Create(PantsErrorCode.NotSupported, "Unknown storage backend.");
        }

        var actor = new Actor(
            options,
            backgroundCompactionEnabled,
            telemetry,
            walMetrics,
            cloudWalUploads,
            cloudFlushRetries,
            storageVerifier,
            verificationBarrierResponse,
            failpoints,
            runtimeTimeProvider,
            runtimeResponses,
            scanMemoryBudget,
            state,
            cloudMode,
            diskStore,
            cloudPersistence,
            cloudCompactionOutputPublisher,
            cloudDdlCoordinator,
            cloudLease,
            cloudLeaseCancellation,
            cloudLeaseHeartbeat,
            walCloudDurableSequence);
        await actor.FinishStartupAsync(options, telemetry, dependencies, startupPhases, cancellationToken)
            .ConfigureAwait(false);
        return actor;
    }

    /// <summary>
    ///     Completes startup on the already-constructed <see cref="Actor" />: runs cloud-WAL recovery
    ///     and starts the background workers. This must run as an instance method (rather than folding
    ///     into the static <see cref="OpenAsync" /> factory) because recovery calls genuine instance
    ///     members such as <see cref="EnsureCloudWriteAuthorityValid" /> and
    ///     <see cref="DrainCloudWalBacklogAsync(CancellationToken)" /> that are also used throughout the
    ///     actor's normal operating lifetime.
    /// </summary>
    async ValueTask FinishStartupAsync(
        RuntimePlan options,
        RuntimeTelemetry telemetry,
        RuntimeDependencies dependencies,
        StartupPhaseRecorder startupPhases,
        CancellationToken cancellationToken)
    {
        try
        {
            _hybridCache = options.Storage switch
            {
                PantsStorageConfiguration.SimulatedCloud simulated => new HybridCacheManager(
                    simulated.LocalStorageBudgetBytes ??
                    HybridStorageBudgetPolicy.DefaultMaximumLocalBytes,
                    _failpoints),
                PantsStorageConfiguration.Cloud => new HybridCacheManager(
                    dependencies.HybridLocalStorageBudgetBytes ??
                    HybridStorageBudgetPolicy.DefaultMaximumLocalBytes,
                    _failpoints),
                _ => null
            };

            if (_cloudMode && _diskStore is not null && _cloudPersistence is not null)
            {
                var recoveryDeadline = OperationDeadline.FromBudget(
                    options.RuntimeResponseTimeout,
                    _runtimeTimeProvider);
                SealedWalSegment? recoveredSegment;
                try
                {
                    recoveredSegment = _diskStore.SealActiveWalForCloud(
                        _walMetrics,
                        EnsureCloudWriteAuthorityValid);
                }
                catch (WalCloudSealCompletedException completed)
                {
                    recoveredSegment = completed.Segment;
                }

                if (recoveredSegment is not null)
                {
                    _cloudWalUploads.Admit(recoveredSegment);
                    _diskStore.CompleteCloudWalSeal(recoveredSegment);
                }

                await recoveryDeadline.RunMutationAsync(
                        DrainCloudWalBacklogAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                await recoveryDeadline.RunMutationAsync(
                        _cloudPersistence.CollectObsoleteSstsAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                ApplyPendingPersistenceAnomaly(_state);
            }

            if (_cloudMode && _diskStore is not null)
            {
                _cloudWalSealController = new CloudWalSealController(
                    options.CloudWritePolicy,
                    _runtimeTimeProvider);
                _cloudMemtableSegments = new CloudMemtableSegmentTracker();
                _cloudMemtableSegments.Reinitialize(
                    _state.ActiveMemtableBytes
                        .Where(static entry => entry.Value > 0)
                        .Select(static entry => entry.Key),
                    _diskStore.CurrentWalSegmentId);
            }

            using var serviceStartup = startupPhases.Measure(StartupPhase.ServiceStartup);
            _walRuntime = new WalRuntimeService(
                options.CoordinatorQueueCapacity,
                _diskStore,
                _failpoints,
                _walMetrics,
                EnsureCloudWriteAuthorityValid);
            _flushRuntime = new FlushRuntimeService(
                options.CoordinatorQueueCapacity,
                _diskStore,
                _telemetry);
            _immutableFlushPipeline = new ImmutableFlushPipeline(
                telemetry,
                (frozen, publicationPlan) => _flushRuntime.ScheduleFrozenFlushAsync(
                    frozen,
                    publicationPlan),
                CompleteImmutableFlushAttemptAsync,
                RetryImmutableFlushAttemptAsync,
                () => Volatile.Read(ref _disposed) != 0,
                dependencies.RuntimeTimeProvider);
            _compactionRuntime = new CompactionRuntimeService(
                options.CoordinatorQueueCapacity,
                _diskStore,
                telemetry,
                options.CompactionMemoryPoolBytes);
            _manifestWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _garbageCollectionWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _cloudWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _cloudWalDrainScheduler = new CloudWorkScheduler(
                _cloudWorker,
                DrainCloudWalBacklogWithFailureTrackingAsync,
                _runtimeTimeProvider);
            _cloudMaintenanceScheduler = new CloudWorkScheduler(
                _cloudWorker,
                MirrorCloudStorageWithFailureTrackingAsync,
                _runtimeTimeProvider);
            _runtimeMetricsSnapshotFactory = new RuntimeMetricsSnapshotFactory(
                options,
                telemetry,
                _diskStore,
                _compactionRuntime,
                _cloudWalSealController,
                _cloudMemtableSegments,
                _hybridCache,
                _scanMemoryBudget,
                _runtimeResponses);
            _publishedRuntimeMetrics = _runtimeMetricsSnapshotFactory.Create(
                _state,
                Volatile.Read(ref _walCloudDurableSequence));
            _workersStarted = true;
            _commands = Channel.CreateBounded<IRuntimeCommand>(new BoundedChannelOptions(
                options.CoordinatorQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            using (startupPhases.Measure(StartupPhase.VersionConstruction))
            {
                _currentVersion = _state.CreateVersion(VisibleFilesSnapshot());
            }

            _loopTask = Task.Run(RunLoopAsync, CancellationToken.None);
            if (UsesBackgroundImmutableFlushes)
            {
                _ = ScheduleRecoveredMemtableFlushesAsync();
            }
        }
        catch
        {
            if (_cloudLease is not null)
            {
                await CleanupFailedProviderStartupAsync(
                    (CloudLeaseCoordinator)_cloudLease,
                    _diskStore,
                    _cloudLeaseCancellation,
                    _cloudLeaseHeartbeat).ConfigureAwait(false);
            }
            else
            {
                CleanupFailedDiskStartup(_diskStore);
            }

            if (_cloudPersistence is not null)
            {
                try
                {
                    await _cloudPersistence.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Startup cleanup must not replace the original failure.
                }
            }

            throw;
        }
    }

    internal static async ValueTask DisposeFailedCloudStartupResourcesAsync(
        ProviderCloudPersistence? providerPersistence,
        ProviderObjectStoreSet objectStores)
    {
        try
        {
            if (providerPersistence is not null)
            {
                await providerPersistence.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await objectStores.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Startup cleanup must not replace the original failure.
        }
    }

    static void CleanupFailedDiskStartup(LocalDiskStore? diskStore)
    {
        try
        {
            diskStore?.Dispose();
        }
        catch (Exception)
        {
            // Startup cleanup must not replace the original failure.
        }
    }

    static async ValueTask CleanupFailedProviderStartupAsync(
        CloudLeaseCoordinator cloudLease,
        LocalDiskStore? diskStore,
        CancellationTokenSource? heartbeatCancellation,
        Task? heartbeat)
    {
        try
        {
            heartbeatCancellation?.Cancel();
            if (heartbeat is not null)
            {
                await heartbeat.WaitAsync(ProviderStartupCleanupTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Startup cleanup must not replace the original failure.
        }
        finally
        {
            heartbeatCancellation?.Dispose();
        }

        CleanupFailedDiskStartup(diskStore);

        try
        {
            using var cancellation = new CancellationTokenSource(
                ProviderStartupCleanupTimeout);
            await cloudLease.ReleaseAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed bounded release leaves the lease to expire naturally.
        }

        try
        {
            cloudLease.Dispose();
        }
        catch (Exception)
        {
            // Startup cleanup must not replace the original failure.
        }
    }

    public bool IsSupported(PantsDurability durability) => _cloudMode
        ? durability is PantsDurability.BestEffort or PantsDurability.CloudAsync or PantsDurability.CloudStrict
        : durability is PantsDurability.Sync or PantsDurability.Buffered or PantsDurability.BestEffort;

    public ValueTask<ColumnFamilyIdentity> CreateColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken) =>
        SendWithinDeadlineAsync(
            async (state, deadline) =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                EnsureCloudLeaseValid();
                if (state.ActiveFamilyVersions.TryGetValue(name, out var activeGeneration))
                {
                    return state.FamilyData.Keys.Single(identity =>
                        identity.Name == name && identity.Generation == activeGeneration);
                }

                var generation = state.FamilyGeneration.TryGetValue(name, out var currentGeneration)
                    ? checked(currentGeneration + 1)
                    : 0;
                var id = state.NextColumnFamilyId;
                var created = new ColumnFamilyIdentity(id, name, generation);
                if (_diskStore is not null && _cloudDdlCoordinator is not null)
                {
                    var edit = LocalDiskStore.CreateColumnFamilyEdit(created);
                    await ExecuteCloudWithinDeadlineAsync(
                            deadline,
                            workerCancellationToken => _cloudDdlCoordinator.ExecuteAsync(
                                state,
                                edit,
                                workerCancellationToken),
                            true)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (_diskStore is not null)
                    {
                        await _manifestWorker
                            .ExecuteAsync(() => _diskStore.CreateColumnFamily(created))
                            .ConfigureAwait(false);
                    }

                    state.NextColumnFamilyId = checked(id + 1);
                    state.FamilyGeneration[name] = generation;
                    state.ActiveFamilyVersions[name] = generation;
                    state.FamilyData[created] = RuntimeState.EmptyFamily;
                    state.RangeTombstones[created] = [];
                    state.ActiveMemtableBytes[created] = 0;
                }

                if (_cloudPersistence is not null)
                {
                    try
                    {
                        await ExecuteCloudWithinDeadlineAsync(
                                deadline,
                                _cloudPersistence.MirrorMetadataAndSstsAsync,
                                true)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        MarkPersistenceAnomaly(state);
                    }
                }

                PublishSnapshot(state);
                return created;
            },
            cancellationToken);

    public async ValueTask DropColumnFamilyAsync(
        ColumnFamilyIdentity identity,
        bool discardUnflushed,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (UsesBackgroundImmutableFlushes)
            {
                await WaitForColumnFamilyFlushAsync(
                        identity,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
                _failpoints.Hit(Failpoint.BeforeDropAdmission);
            }

            var dropped = await SendWithinDeadlineAsync(
                async (state, deadline) =>
                {
                    ThrowIfShuttingDown(state);
                    ThrowIfVerificationInProgress();
                    EnsureCloudLeaseValid();
                    ValidateActiveFamily(state, identity);
                    if (UsesBackgroundImmutableFlushes &&
                        state.ImmutableMemtableFlushes.Values.Any(flush =>
                            flush.Frozen.ColumnFamily == identity))
                    {
                        return false;
                    }

                    if (_cloudDdlCoordinator is not null)
                    {
                        await ExecuteCloudWithinDeadlineAsync(
                                deadline,
                                workerCancellationToken => _cloudDdlCoordinator.ReconcilePendingAsync(
                                    state,
                                    workerCancellationToken),
                                true)
                            .ConfigureAwait(false);
                        if (!IsActiveFamily(state, identity))
                        {
                            PublishSnapshot(state);
                            return true;
                        }
                    }

                    if (!discardUnflushed && state.UnflushedFamilies.Contains(identity))
                    {
                        throw PantsException.Create(
                            PantsErrorCode.Busy,
                            $"Column family '{identity.Name}' has committed data that has not been flushed.");
                    }

                    if (_diskStore is not null && _cloudDdlCoordinator is not null)
                    {
                        var edit = _diskStore.CreateDropColumnFamilyEdit(state, identity);
                        await ExecuteCloudWithinDeadlineAsync(
                                deadline,
                                workerCancellationToken => _cloudDdlCoordinator.ExecuteAsync(
                                    state,
                                    edit,
                                    workerCancellationToken),
                                true)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        if (_diskStore is not null)
                        {
                            await _manifestWorker
                                .ExecuteAsync(() => _diskStore.DropColumnFamily(state, identity))
                                .ConfigureAwait(false);
                        }

                        state.ActiveFamilyVersions.Remove(identity.Name);
                        state.FamilyData.Remove(identity);
                        state.RangeTombstones.Remove(identity);
                        state.ActiveMemtableBytes.Remove(identity);
                        state.UnflushedFamilies.Remove(identity);
                        state.SignalWritePressureChanged();
                    }

                    _cloudMemtableSegments?.RecordFlush(identity);

                    if (_cloudPersistence is not null)
                    {
                        try
                        {
                            await ExecuteCloudWithinDeadlineAsync(
                                    deadline,
                                    _cloudPersistence.MirrorMetadataAndSstsAsync,
                                    true)
                                .ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            MarkPersistenceAnomaly(state);
                        }
                    }

                    if (_diskStore is not null)
                    {
                        DeferObsoleteFileCollectionUntilSnapshotsRelease(state);

                        var storageChanged = false;
                        await _garbageCollectionWorker
                            .ExecuteAsync(() =>
                                storageChanged = _diskStore.CollectObsoleteFiles(state))
                            .ConfigureAwait(false);
                        if (_cloudPersistence is not null &&
                            (storageChanged || _cloudPersistence.HasPersistenceAnomaly))
                        {
                            try
                            {
                                await ExecuteCloudWithinDeadlineAsync(
                                        deadline,
                                        _cloudPersistence.MirrorMetadataAndSstsAsync,
                                        true)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                MarkPersistenceAnomaly(state);
                            }
                        }
                    }

                    PublishSnapshot(state);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            if (dropped)
            {
                _writeStallHints.TryRemove(identity, out _);
                return;
            }
        }
    }

    public ValueTask<ColumnFamilyIdentity?> GetActiveColumnFamilyIdentityAsync(
        string name,
        CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                var matches = state.FamilyData.Keys
                    .Where(candidate =>
                        candidate.Name == name &&
                        state.ActiveFamilyVersions.TryGetValue(name, out var generation) &&
                        candidate.Generation == generation)
                    .ToArray();
                return ValueTask.FromResult<ColumnFamilyIdentity?>(matches.Length switch
                {
                    0 => null,
                    1 => matches[0],
                    _ => throw PantsException.Create(
                        PantsErrorCode.Internal,
                        $"Multiple active identities exist for column family '{name}'.")
                });
            },
            cancellationToken);

    public ValueTask<IReadOnlyList<ColumnFamilyIdentity>> ListColumnFamiliesAsync(
        CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<ColumnFamilyIdentity>>(
            state => ValueTask.FromResult<IReadOnlyList<ColumnFamilyIdentity>>(
                state.FamilyData.Keys.ToArray()),
            cancellationToken);

    public ValueTask<long> RegisterScanSnapshotAsync(
        DatabaseVersion snapshot,
        CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                ThrowIfShuttingDown(state);
                var snapshotId = checked(++state.TransactionCounter);
                state.ActiveScanSnapshots[snapshotId] = new ScanSnapshotPin(
                    snapshotId,
                    snapshot.Sequence,
                    state.Clock.UtcNow,
                    snapshot);
                _telemetry.RecordSnapshotRegister();
                return ValueTask.FromResult(snapshotId);
            },
            cancellationToken,
            snapshotId => _ = ReleaseAbandonedScanSnapshotAsync(snapshotId));

    public async ValueTask ReleaseScanSnapshotAsync(
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await SendWithinDeadlineAsync(
            async (state, deadline) =>
            {
                if (state.ActiveScanSnapshots.Remove(snapshotId))
                {
                    _telemetry.RecordSnapshotUnregister();
                    await CollectObsoleteFilesAfterSnapshotReleaseAsync(state)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IPantsTransaction> BeginTransactionAsync(
        DatabaseInstance database,
        ColumnFamilyHandle columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                ThrowIfShuttingDown(state);
                var identity = columnFamily.Identity;
                ValidateActiveFamily(state, identity);
                var transactionId = checked(++state.TransactionCounter);
                var snapshot = Volatile.Read(ref _currentVersion);
                var transaction = new TransactionInstance(
                    database,
                    transactionId,
                    columnFamily,
                    mode,
                    snapshot,
                    state.Clock.UtcNow,
                    _diskStore?.RootPath);
                state.ActiveTransactions[transactionId] = new TransactionInfo(
                    transactionId,
                    mode,
                    snapshot.Sequence,
                    state.Clock.UtcNow,
                    snapshot);
                _telemetry.RecordTransactionBegin(mode);
                return ValueTask.FromResult<IPantsTransaction>(transaction);
            },
            cancellationToken,
            transaction => _ = DisposeAbandonedTransactionAsync(transaction));

    public IPantsTransaction BeginReadOnlyTransaction(
        DatabaseInstance database,
        ColumnFamilyHandle columnFamily,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_directReadAdmissionGate)
        {
            if (Volatile.Read(ref _shutdownRequested))
            {
                throw new PantsBusyException("The runtime is shutting down.");
            }

            var version = Volatile.Read(ref _currentVersion);
            var identity = columnFamily.Identity;
            if (!version.ActiveColumnFamilyVersions.TryGetValue(identity.Name, out var generation) ||
                generation != identity.Generation ||
                !version.Families.ContainsKey(identity))
            {
                throw PantsException.Create(
                    PantsErrorCode.InvalidArgument,
                    $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
            }

            var transactionId = Interlocked.Decrement(ref _state.DirectReadOnlyTransactionCounter);
            var startedAt = _state.Clock.UtcNow;
            var transaction = new TransactionInstance(
                database,
                transactionId,
                columnFamily,
                PantsTransactionMode.ReadOnly,
                version,
                startedAt,
                null,
                false);
            if (!_state.DirectReadOnlyTransactions.TryAdd(
                    transactionId,
                    new TransactionInfo(
                        transactionId,
                        PantsTransactionMode.ReadOnly,
                        version.Sequence,
                        startedAt,
                        version)))
            {
                throw new PantsInternalException("A direct read-only transaction identifier collided.");
            }

            _telemetry.RecordTransactionBegin(PantsTransactionMode.ReadOnly);
            return transaction;
        }
    }

    public ValueTask RecordReadOnlyTransactionCommitAsync(long transactionId) =>
        CompleteReadOnlyTransactionAsync(transactionId, true);

    public ValueTask RecordReadOnlyTransactionRollbackAsync(long transactionId) =>
        CompleteReadOnlyTransactionAsync(transactionId, false);

    async ValueTask CompleteReadOnlyTransactionAsync(long transactionId, bool committed)
    {
        if (!_state.DirectReadOnlyTransactions.TryRemove(transactionId, out _))
        {
            return;
        }

        _telemetry.RecordSnapshotUnregister();
        if (committed)
        {
            _telemetry.RecordTransactionCommit();
        }
        else
        {
            _telemetry.RecordTransactionRollback();
        }

        if (Volatile.Read(ref _snapshotReleaseCollectionRequired) == 0 ||
            _state.ActiveSnapshotCount != 0)
        {
            return;
        }

        await SendAsync(
            async state =>
            {
                if (state.ActiveSnapshotCount == 0)
                {
                    Volatile.Write(ref _snapshotReleaseCollectionRequired, 0);
                    await CollectObsoleteFilesAfterSnapshotReleaseAsync(state).ConfigureAwait(false);
                }

                return true;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        CancellationToken cancellationToken)
    {
        var delegated = false;
        try
        {
            if (payload.Mode != PantsTransactionMode.ReadOnly &&
                !IsSupported(writeOptions.Durability))
            {
                throw PantsException.Create(
                    PantsErrorCode.InvalidArgument,
                    $"Durability '{writeOptions.Durability}' is not valid for this storage backend.");
            }

            if (RequiresWriteAdmission(payload))
            {
                var families = GetCommitFamilies(payload);
                if (families.Any(_writeStallHints.ContainsKey))
                {
                    await EnsureWriteAdmissionAsync(families, cancellationToken).ConfigureAwait(false);
                }
            }

            delegated = true;
            await SendCommitAsync(
                writeOptions,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
            {
                payload.Dispose();
            }
        }
    }

    public async ValueTask RollbackAsync(long transactionId, CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                if (state.ActiveTransactions.Remove(transactionId))
                {
                    _telemetry.RecordSnapshotUnregister();
                    _telemetry.RecordTransactionRollback();
                    await CollectObsoleteFilesAfterSnapshotReleaseAsync(state)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FlushAsync(
        ColumnFamilyIdentity identity,
        CancellationToken cancellationToken)
    {
        if (UsesBackgroundImmutableFlushes)
        {
            await FlushImmutableMemtableAsync(identity, cancellationToken).ConfigureAwait(false);
            _failpoints.Hit(Failpoint.BeforeFlushCompactionAdmission);
            _ = await SendAsync(
                async state =>
                {
                    await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
                    PublishSnapshot(state);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendWithinDeadlineAsync(
            async (state, deadline) =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                EnsureCloudWriteAuthorityValid();
                ValidateActiveFamily(state, identity);
                if (_diskStore is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    await _flushRuntime
                        .FlushAsync(state, identity)
                        .ConfigureAwait(false);
                    _telemetry.RecordFlush(Stopwatch.GetElapsedTime(started));
                }

                try
                {
                    await MirrorCloudStorageAsync(deadline).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    _cloudPersistence is not null &&
                    exception is not OperationCanceledException)
                {
                    _telemetry.RecordFlushFailure();
                    MarkPersistenceAnomaly(state);
                    if (exception is IOException or PantsIOException or PantsTimeoutException)
                    {
                        ScheduleCloudFlushRetry(identity);
                    }

                    throw;
                }

                CompleteCloudFlush(state, identity);
                await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
                PublishSnapshot(state);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompactAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _activeCompactionRequests);
        try
        {
            await CompactCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionRequests);
        }
    }

    async ValueTask CompactCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (UsesBackgroundImmutableFlushes)
            {
                await FlushActiveAndImmutableMemtablesAsync(cancellationToken)
                    .ConfigureAwait(false);
                _failpoints.Hit(Failpoint.BeforeCompactionAdmission);
            }

            var compacted = await SendWithinDeadlineAsync(
                async (state, deadline) =>
                {
                    ThrowIfShuttingDown(state);
                    ThrowIfVerificationInProgress();
                    EnsureCloudWriteAuthorityValid();
                    if (UsesBackgroundImmutableFlushes)
                    {
                        foreach (var family in state.ActiveMemtableBytes
                                     .Where(static pair => pair.Value > 0)
                                     .Select(static pair => pair.Key)
                                     .ToArray())
                        {
                            _ = await FreezeAndScheduleFlushAsync(state, family)
                                .ConfigureAwait(false);
                        }

                        if (state.ImmutableMemtableFlushes.Count != 0 ||
                            state.UnflushedFamilies.Count != 0)
                        {
                            return false;
                        }
                    }

                    if (_diskStore is not null)
                    {
                        await deadline.RunAsync(
                                token => EnsureHybridSstsLocalAsync(
                                    _diskStore.GetCompactionInputNames(state, true),
                                    token),
                                cancellationToken)
                            .ConfigureAwait(false);
                        var result = await deadline.RunMutationAsync(
                                token => _compactionRuntime.CompactAsync(
                                    state,
                                    true,
                                    _cloudCompactionOutputPublisher,
                                    prepareInputs: EnsureHybridSstsLocalAsync,
                                    cancellationToken: token),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (result.PersistenceAnomaly)
                        {
                            Volatile.Write(ref _persistenceAnomaly, 1);
                            MarkPersistenceAnomaly(state);
                        }

                        PublishSnapshot(state);
                    }

                    await MirrorCloudStorageAsync(deadline).ConfigureAwait(false);
                    _backgroundCompactionPending = false;
                    _readAmplificationCompactionPending = false;
                    state.UnflushedFamilies.Clear();
                    ClearMemtableAccounting(state);
                    _cloudMemtableSegments?.Reinitialize(
                        [],
                        _diskStore?.CurrentWalSegmentId ?? 0);
                    PublishSnapshot(state);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            if (compacted)
            {
                return;
            }
        }
    }

    public async ValueTask SetBackgroundCompactionAsync(bool enabled, CancellationToken cancellationToken)
    {
        await SendAsync(
            state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                _backgroundCompactionEnabled = enabled;
                return ValueTask.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> WaitForWriteStallClearAsync(
        ColumnFamilyIdentity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Write-stall timeout must not be negative.");
        }

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (Volatile.Read(ref _shutdownRequested))
            {
                throw new PantsBusyException("The runtime is shutting down.");
            }

            var status = await SendAsync(
                state =>
                {
                    ThrowIfShuttingDown(state);
                    ValidateActiveFamily(state, identity);
                    return ValueTask.FromResult(new WritePressureWaitStatus(
                        MemtableWritePressure.IsStalled(_options, state, [identity]),
                        state.WritePressureChanged));
                },
                cancellationToken).ConfigureAwait(false);
            if (!status.IsStalled)
            {
                return true;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await status.StateChanged.WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    async ValueTask RelieveWritePressureAsync(
        RuntimeState state,
        CommitPayload payload,
        OperationDeadline deadline)
    {
        if (_diskStore is null)
        {
            return;
        }

        var operationBytes = GetOperationBytesByFamily(payload);
        var familiesOverLimit = operationBytes
            .Where(pair =>
                state.ActiveMemtableBytes.GetValueOrDefault(pair.Key) > 0 &&
                state.ActiveMemtableBytes.GetValueOrDefault(pair.Key) + pair.Value >
                _options.MemtableSizeLimitBytes)
            .Select(static pair => pair.Key)
            .ToArray();
        if (familiesOverLimit.Length == 0)
        {
            return;
        }

        if (UsesBackgroundImmutableFlushes)
        {
            foreach (var family in familiesOverLimit)
            {
                await FreezeAndScheduleFlushAsync(
                        state,
                        family,
                        false)
                    .ConfigureAwait(false);
            }

            return;
        }

        await _flushRuntime.FlushAsync(state).ConfigureAwait(false);
        await MirrorCloudStorageAsync(deadline).ConfigureAwait(false);
        state.UnflushedFamilies.Clear();
        ClearMemtableAccounting(state);
        await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
    }

    public ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (HasLiveRuntimeMetrics())
        {
            return ValueTask.FromResult(_runtimeMetricsSnapshotFactory.RefreshLive(
                Volatile.Read(ref _publishedRuntimeMetrics),
                Volatile.Read(ref _activeCompactionRequests),
                Volatile.Read(ref _walCloudDurableSequence)));
        }

        return SendAsync(
            state =>
            {
                _failpoints.Hit(Failpoint.BeforeRuntimeMetricsResponse);
                ApplyPendingPersistenceAnomaly(state);
                var metrics = _runtimeMetricsSnapshotFactory.Create(
                    state,
                    Volatile.Read(ref _walCloudDurableSequence));
                Volatile.Write(ref _publishedRuntimeMetrics, metrics);
                return ValueTask.FromResult(metrics);
            },
            cancellationToken);
    }

    public ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(
            _ => ValueTask.FromResult(_telemetry.GetReadAmplificationMetrics()),
            cancellationToken);

    public ValueTask<PantsReadPathDiagnostics> GetReadPathDiagnosticsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(
            _ => ValueTask.FromResult(_telemetry.GetReadPathDiagnostics()),
            cancellationToken);

    /// <summary>
    ///     Resolves a key's newest visible SST entry from a local or remote SST source, or
    ///     <c>null</c> if none of <paramref name="candidatesNewestFirst" /> contain it. Called
    ///     directly off the caller's thread (not the actor mailbox) — safe because it only reads
    ///     the lock-free reader/block caches, mirroring <see cref="RecordPointReadAsync" />'s
    ///     existing non-mailbox fast path.
    /// </summary>
    public async ValueTask<SstEntry?> TryReadPointValueAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        if (_diskStore is null)
        {
            return null;
        }

        if (_hybridCache is null)
        {
            return _diskStore.TryReadPointValue(candidatesNewestFirst, key.Span);
        }

        if (candidatesNewestFirst.Any(file => !_diskStore.IsSstLocal(file.Name)))
        {
            // The failpoint historically guarded whole-file hydration. It now guards admission
            // to the equivalent remote ranged-read path.
            await Task.Yield();
            _failpoints.Hit(Failpoint.BeforeHybridSstHydration);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await _diskStore.TryReadPointValueAsync(
                candidatesNewestFirst,
                key,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Opens manifest-selected local or remote SST sources for a scan's asynchronous k-way
    ///     merge. Remote files remain authoritative without being admitted to the local cache.
    /// </summary>
    public async ValueTask<IReadOnlyList<AsyncSstScanSource>> CreateScanSourcesAsync(
        IReadOnlyList<FileMeta> candidates,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        CancellationToken cancellationToken)
    {
        if (_diskStore is null)
        {
            return [];
        }

        if (_hybridCache is not null && candidates.Any(file => !_diskStore.IsSstLocal(file.Name)))
        {
            await Task.Yield();
            _failpoints.Hit(Failpoint.BeforeHybridSstHydration);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await _diskStore.CreateScanSourcesAsync(
                candidates,
                direction,
                startInclusive,
                endExclusive,
                _scanMemoryBudget,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RecordPointReadAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hybridCache is null)
        {
            var exceedsBudget = _diskStore is null
                ? _telemetry.RecordSstRead(default)
                : _diskStore.RecordPointRead(_telemetry, columnFamily, key.Span);
            if (!exceedsBudget || !_backgroundCompactionEnabled || _diskStore is null)
            {
                return;
            }

            _ = await SendAsync(
                async state =>
                {
                    await RunReadAmplificationCompactionAsync(state).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await RecordPointReadCoreAsync(
            columnFamily,
            key,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PantsPointReadTrace> RecordPointReadWithDiagnosticsAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken) =>
        await RecordPointReadCoreAsync(
            columnFamily,
            key,
            true,
            cancellationToken).ConfigureAwait(false) ??
        throw new PantsInternalException("Point-read diagnostics were not captured.");

    async ValueTask<PantsPointReadTrace?> RecordPointReadCoreAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        bool captureDiagnostics,
        CancellationToken cancellationToken) =>
        await SendAsync(
            async state =>
            {
                bool exceedsBudget;
                PantsPointReadTrace? trace = null;
                if (_diskStore is null)
                {
                    exceedsBudget = _telemetry.RecordSstRead(default);
                    if (captureDiagnostics)
                    {
                        trace = new PantsPointReadTrace(0, []);
                    }
                }
                else if (_hybridCache is not null)
                {
                    var result = await _diskStore.RecordPointReadAsync(
                            _telemetry,
                            columnFamily,
                            key,
                            cancellationToken)
                        .ConfigureAwait(false);
                    exceedsBudget = result.ExceedsBudget;
                    if (captureDiagnostics)
                    {
                        trace = result.Trace;
                    }
                }
                else
                {
                    if (captureDiagnostics)
                    {
                        exceedsBudget = _diskStore.RecordPointRead(
                            _telemetry,
                            columnFamily,
                            key.Span,
                            null,
                            out trace);
                    }
                    else
                    {
                        exceedsBudget = _diskStore.RecordPointRead(
                            _telemetry,
                            columnFamily,
                            key.Span);
                    }
                }

                if (exceedsBudget && _backgroundCompactionEnabled && _diskStore is not null)
                {
                    await RunReadAmplificationCompactionAsync(state).ConfigureAwait(false);
                }

                return trace;
            },
            cancellationToken).ConfigureAwait(false);

    public IScanReadValidator? CreateScanReadValidator(
        IReadOnlyList<AsyncSstScanSource> sources) =>
        sources.Count == 0 ? null : new AsyncSstScanReadValidator(_telemetry, sources);

    public ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(
            _ => ValueTask.FromResult(_telemetry.GetRecoveryMetrics()),
            cancellationToken);

    public ValueTask<PantsStorageLayout> GetStorageLayoutAsync(CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                var layout = _diskStore?.GetStorageLayout(state) ?? EmptyStorageLayout(state);
                return ValueTask.FromResult(layout with
                {
                    Health = EngineHealthClassifier.Classify(
                        layout.Health,
                        MemtableWritePressure.IsStalled(_options, state))
                });
            },
            cancellationToken);

    public async ValueTask<PantsStorageVerificationReport> VerifyStorageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Verification timeout must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_diskStore is null)
        {
            throw PantsException.Create(
                PantsErrorCode.NotSupported,
                "In-memory storage has no persistent path to verify.");
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        using var acquisitionCancellation = new CancellationTokenSource();
        var acquisition = AcquireVerificationBarrierAsync(acquisitionCancellation.Token).AsTask();
        OnlineVerificationBarrier barrier;
        try
        {
            barrier = await acquisition.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            acquisitionCancellation.Cancel();
            _ = ReleaseAbandonedVerificationBarrierAsync(acquisition);
            throw CreateVerificationTimeoutException();
        }
        catch (OperationCanceledException)
        {
            acquisitionCancellation.Cancel();
            _ = ReleaseAbandonedVerificationBarrierAsync(acquisition);
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await ReleaseVerificationBarrierAsync(barrier).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (deadline.IsCancellationRequested)
        {
            await ReleaseVerificationBarrierAsync(barrier).ConfigureAwait(false);
            throw CreateVerificationTimeoutException();
        }

        var verification = Task.Factory.StartNew(
                () => RunStorageVerificationAsync(barrier),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
        try
        {
            return await verification.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _ = ObserveStorageVerificationAsync(verification);
            throw CreateVerificationTimeoutException();
        }
        catch (OperationCanceledException)
        {
            _ = ObserveStorageVerificationAsync(verification);
            throw;
        }
        catch (Exception exception)
        {
            ExceptionDispatchInfo.Capture(
                RuntimeExceptionMapper.ToPublicException(exception, cancellationToken)).Throw();
            throw;
        }
    }

    async ValueTask<OnlineVerificationBarrier> AcquireVerificationBarrierAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var admission = await SendAsync(
                async state =>
                {
                    ThrowIfShuttingDown(state);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_verificationBarrier is not null)
                    {
                        return (
                            Barrier: null,
                            RetryAfter: (Task?)_verificationBarrier.Released,
                            LayoutBusy: false);
                    }

                    if (_verificationMaintenanceCompletion is { } maintenanceCompletion)
                    {
                        return (
                            Barrier: null,
                            RetryAfter: (Task?)maintenanceCompletion.Task,
                            LayoutBusy: false);
                    }

                    if (HasLayoutMutationInFlight(state))
                    {
                        return (
                            Barrier: null,
                            RetryAfter: null,
                            LayoutBusy: true);
                    }

                    if (_hybridCache is not null && _diskStore is not null)
                    {
                        await EnsureHybridSstsLocalAsync(
                                _diskStore.GetManifestSstNames(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var token = checked(++_nextVerificationBarrierToken);
                    var barrier = new OnlineVerificationBarrier(
                        token,
                        _diskStore?.RootPath,
                        CaptureRuntimeHealth(state));
                    _verificationBarrier = barrier;
                    try
                    {
                        _failpoints.Hit(Failpoint.BeforeVerificationBarrierResponse);
                        await _verificationBarrierResponse().ConfigureAwait(false);
                    }
                    catch
                    {
                        _verificationBarrier = null;
                        barrier.Release();
                        throw;
                    }

                    return (
                        Barrier: (OnlineVerificationBarrier?)barrier,
                        RetryAfter: (Task?)null,
                        LayoutBusy: false);
                },
                CancellationToken.None).ConfigureAwait(false);
            if (admission.Barrier is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await ReleaseVerificationBarrierAsync(admission.Barrier).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return admission.Barrier;
            }

            if (admission.RetryAfter is not null)
            {
                await admission.RetryAfter.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (admission.LayoutBusy && UsesBackgroundImmutableFlushes)
            {
                await DrainImmutableFlushesAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    async Task<PantsStorageVerificationReport> RunStorageVerificationAsync(
        OnlineVerificationBarrier barrier)
    {
        try
        {
            var report = await _storageVerifier(barrier.Path!, CancellationToken.None)
                .ConfigureAwait(false);
            return report with
            {
                Authoritative = !_cloudMode && report.Authoritative,
                Health = barrier.RuntimeHealth == PantsEngineHealth.Healthy
                    ? report.Health
                    : barrier.RuntimeHealth
            };
        }
        finally
        {
            await ReleaseVerificationBarrierAsync(barrier).ConfigureAwait(false);
        }
    }

    async Task ReleaseAbandonedVerificationBarrierAsync(
        Task<OnlineVerificationBarrier> acquisition)
    {
        try
        {
            var barrier = await acquisition.ConfigureAwait(false);
            await ReleaseVerificationBarrierAsync(barrier).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller no longer owns an acquisition response that arrives later.
        }
    }

    static async Task ObserveStorageVerificationAsync(
        Task<PantsStorageVerificationReport> verification)
    {
        try
        {
            _ = await verification.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The barrier-owning verifier outlived its caller and releases itself.
        }
    }

    static PantsTimeoutException CreateVerificationTimeoutException() =>
        new("Storage verification did not complete before its deadline.");

    bool HasLayoutMutationInFlight(RuntimeState state) =>
        state.ImmutableMemtableFlushes.Count != 0 ||
        _walRuntime.Outstanding != 0 ||
        _flushRuntime.Outstanding != 0 ||
        _compactionRuntime.Outstanding != 0 ||
        _manifestWorker.Outstanding != 0 ||
        _garbageCollectionWorker.Outstanding != 0 ||
        _cloudWorker.Outstanding != 0 ||
        _cloudWalDrainScheduler.Outstanding != 0 ||
        _cloudMaintenanceScheduler.Outstanding != 0;

    PantsEngineHealth CaptureRuntimeHealth(RuntimeState state)
    {
        var storageHealth = _diskStore?.GetHealth(state) ?? state.Health;
        var health = EngineHealthClassifier.Classify(
            storageHealth,
            MemtableWritePressure.IsStalled(_options, state));
        return health == PantsEngineHealth.Healthy && !IsPrimaryLeaseHealthy
            ? PantsEngineHealth.Degraded
            : health;
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admission = SendAsync<Task>(
            state =>
            {
                lock (_directReadAdmissionGate)
                {
                    if (!_shutdownRequested)
                    {
                        if (state.ActiveSnapshotCount != 0)
                        {
                            throw PantsException.Create(
                                PantsErrorCode.Busy,
                                "Database shutdown is blocked by active transactions or scans.");
                        }

                        Volatile.Write(ref _shutdownRequested, true);
                        state.IsShuttingDown = true;
                        state.ActiveTransactions.Clear();
                        state.DirectReadOnlyTransactions.Clear();
                        state.ActiveScanSnapshots.Clear();
                        foreach (var flush in state.ImmutableMemtableFlushes.Values)
                        {
                            flush.FailWaiterForShutdown();
                        }

                        state.SignalWritePressureChanged();
                    }
                }

                return ValueTask.FromResult(
                    _verificationBarrier?.Released ?? Task.CompletedTask);
            },
            CancellationToken.None,
            enforceRuntimeResponseTimeout: false).AsTask();
        try
        {
            var verificationReleased = await admission
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await verificationReleased.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveShutdownPreparationAsync(admission);
            throw;
        }

        var preparation = SendAsync(
            async state =>
            {
                if (_shutdownPreparationCompleted)
                {
                    return true;
                }

                if (_diskStore is not null)
                {
                    _ = await _walRuntime.FlushDurabilityBoundaryAsync(
                            Failpoint.BeforeShutdownWalDurabilityBoundary)
                        .ConfigureAwait(false);
                    if (_cloudPersistence is not null && (_cloudLease?.IsHealthy ?? true))
                    {
                        var seal = await SealWalForCloudAsync()
                            .ConfigureAwait(false);
                        if (seal.Segment is not null)
                        {
                            _cloudWalSealController?.RecordSeal();
                            CancelCloudWalSealDeadline();
                        }

                        await TryDrainCloudWalBacklogOnShutdownAsync(state)
                            .ConfigureAwait(false);
                    }
                }

                if (_cloudLease?.IsHealthy ?? true)
                {
                    await TryMirrorCloudStorageOnShutdownAsync(state).ConfigureAwait(false);
                }

                _shutdownPreparationCompleted = true;
                return true;
            },
            CancellationToken.None,
            enforceRuntimeResponseTimeout: false).AsTask();
        try
        {
            await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveShutdownPreparationAsync(preparation);
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                _ = await SendAsync(
                        state =>
                        {
                            Volatile.Write(ref _shutdownRequested, false);
                            state.IsShuttingDown = false;
                            state.SignalWritePressureChanged();
                            return ValueTask.FromResult(true);
                        },
                        CancellationToken.None,
                        enforceRuntimeResponseTimeout: false)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the preparation failure; disposal remains available as a fallback.
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        if (UsesBackgroundImmutableFlushes)
        {
            await WaitForImmutableFlushWorkersAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task ObserveShutdownPreparationAsync(Task preparation)
    {
        try
        {
            await preparation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A timed-out caller no longer owns this admitted durability work.
        }
    }

    static async Task RunCloudLeaseHeartbeatAsync(
        CloudLeaseCoordinator lease,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await lease.RenewAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (PantsException)
        {
            // The coordinator marks the lease unhealthy and invokes the configured callback.
        }
    }

    ValueTask<T> SendAsync<T>(
        Func<RuntimeState, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        Action<T>? abandonedResponse = null,
        bool enforceRuntimeResponseTimeout = true,
        [CallerMemberName] string requestKind = "RuntimeCommand") =>
        SendWithinDeadlineAsync(
            (state, _) => operation(state),
            cancellationToken,
            abandonedResponse,
            enforceRuntimeResponseTimeout,
            requestKind);

    async ValueTask<T> SendWithinDeadlineAsync<T>(
        Func<RuntimeState, OperationDeadline, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        Action<T>? abandonedResponse = null,
        bool enforceRuntimeResponseTimeout = true,
        [CallerMemberName] string requestKind = "RuntimeCommand")
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is disposed.");
        }

        var requestId = NextRuntimeRequestId();
        var started = Stopwatch.GetTimestamp();
        var deadline = OperationDeadline.FromStart(
            _runtimeTimeProvider.GetTimestamp(),
            _options.RuntimeResponseTimeout,
            _runtimeTimeProvider);
        var command = new RuntimeCommand<T>(
            state => operation(state, deadline),
            _runtimeResponses,
            requestId,
            requestKind,
            abandonedResponse,
            cancellationToken);
        Interlocked.Increment(ref _commandsEnqueued);
        var response = command.Response;
        var admitted = false;
        Interlocked.Increment(ref _queuedCommands);
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            admitted = true;
            try
            {
                if (!enforceRuntimeResponseTimeout)
                {
                    return await response.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return await response.WaitAsync(
                        _options.RuntimeResponseTimeout,
                        _runtimeTimeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (command.AbandonResponse(_options.RuntimeResponseTimeout))
                {
                    throw CreateRuntimeResponseTimeout(requestKind, requestId);
                }

                return await response.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested &&
                command.AbandonResponse(_options.RuntimeResponseTimeout))
            {
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            if (!admitted)
            {
                _telemetry.RecordCommandRejected();
            }

            throw;
        }
        catch (PantsNoSpaceException)
        {
            _telemetry.RecordWriteStallNoSpace();
            throw;
        }
        catch (ChannelClosedException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Aborted,
                "The Pants runtime is closed.",
                exception);
        }
        finally
        {
            command.UnregisterResponse();
            Interlocked.Decrement(ref _queuedCommands);
            _telemetry.RecordCommandLatency(Stopwatch.GetElapsedTime(started));
        }
    }

    async ValueTask SendCommitAsync(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        CancellationToken cancellationToken)
    {
        CommitRuntimeCommand? command = null;
        ColumnFamilyIdentity[] commitFamilies = [];
        var requestId = 0L;
        var admitted = false;
        var queued = false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is disposed.");
            }

            if (RequiresWriteAdmission(payload))
            {
                ThrowIfRuntimeWorkerWriteStalled();
            }

            commitFamilies = GetCommitFamilies(payload);
            requestId = NextRuntimeRequestId();
            var operationDeadline = OperationDeadline.FromStart(
                _runtimeTimeProvider.GetTimestamp(),
                _options.RuntimeResponseTimeout,
                _runtimeTimeProvider);
            command = new CommitRuntimeCommand(
                writeOptions,
                payload,
                state => ExecuteCommitAsync(state, writeOptions, payload, operationDeadline),
                _runtimeResponses,
                requestId,
                nameof(CommitAsync),
                operationDeadline);
            Interlocked.Increment(ref _queuedCommands);
            queued = true;
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            admitted = true;
            bool writeStalled;
            try
            {
                writeStalled = await command.Task.WaitAsync(
                        _options.RuntimeResponseTimeout,
                        _runtimeTimeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (command.AbandonResponse(_options.RuntimeResponseTimeout))
                {
                    throw CreateRuntimeResponseTimeout(nameof(CommitAsync), requestId);
                }

                writeStalled = await command.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested &&
                command.AbandonResponse(_options.RuntimeResponseTimeout))
            {
                throw;
            }

            if (writeStalled)
            {
                foreach (var family in commitFamilies)
                {
                    _writeStallHints.TryAdd(family, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!admitted)
            {
                _telemetry.RecordCommandRejected();
            }

            throw;
        }
        catch (PantsNoSpaceException)
        {
            _telemetry.RecordWriteStallNoSpace();
            throw;
        }
        catch (ChannelClosedException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Aborted,
                "The Pants runtime is closed.",
                exception);
        }
        finally
        {
            command?.UnregisterResponse();
            if (queued)
            {
                Interlocked.Decrement(ref _queuedCommands);
            }

            if (!admitted)
            {
                if (command is null)
                {
                    payload.Dispose();
                }
                else
                {
                    command.DisposePayload();
                }
            }

            _telemetry.RecordCommandLatency(Stopwatch.GetElapsedTime(started));
        }
    }

    long NextRuntimeRequestId()
    {
        var requestId = Interlocked.Increment(ref _nextRuntimeRequestId);
        if (requestId <= 0)
        {
            throw new PantsInternalException("The runtime request ID space is exhausted.");
        }

        return requestId;
    }

    PantsTimeoutException CreateRuntimeResponseTimeout(string requestKind, long requestId) => new(
        $"Runtime request '{requestKind}' with request ID {requestId} exceeded " +
        $"RuntimeResponseTimeout {_options.RuntimeResponseTimeout:c} after admission; " +
        "the operation may still complete, its outcome is unknown, and it is not automatically " +
        "safe to retry.",
        null,
        true);

    static async Task DisposeAbandonedTransactionAsync(IPantsTransaction transaction)
    {
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The runtime retains ownership of shutdown cleanup after response abandonment.
        }
    }

    async Task ReleaseAbandonedScanSnapshotAsync(long snapshotId)
    {
        try
        {
            await ReleaseScanSnapshotAsync(snapshotId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The runtime retains ownership of shutdown cleanup after response abandonment.
        }
    }

    void ThrowIfRuntimeWorkerWriteStalled()
    {
        ThrowIfCloudWalUploadWriteStalled();

        if (_hybridCache is not null &&
            _compactionRuntime.Outstanding >= _options.CoordinatorQueueCapacity)
        {
            _telemetry.RecordWriteStallCompaction();
            throw new PantsWriteStallException(
                "Writes are stalled while the bounded hybrid compaction queue is full.");
        }
    }

    void ThrowIfCloudWalUploadWriteStalled()
    {
        if (!_cloudMode || _cloudWalUploads.Count < _options.CoordinatorQueueCapacity)
        {
            return;
        }

        _telemetry.RecordWriteStallCloud();
        throw new PantsWriteStallException(
            "Writes are stalled while the bounded cloud upload queue is full.");
    }

    async Task RunLoopAsync()
    {
        var commitCoalescer = new CommitCoalescer(
            _diskStore is not null &&
            _options.FlushAfterWalRecords == 0,
            _options.MemtableSizeLimitBytes,
            _telemetry,
            (commits, state, durability, beforeSync) => _walRuntime.AppendCommitGroupAsync(
                commits,
                state,
                durability,
                beforeSync),
            ApplyOperation);
        try
        {
            await foreach (var command in _commands.Reader
                               .ReadAllAsync(_loopCancellation.Token)
                               .ConfigureAwait(false))
            {
                ApplyPendingPersistenceAnomaly(_state);
                if (command is not CommitRuntimeCommand firstCommit)
                {
                    await command.ExecuteAsync(_state).ConfigureAwait(false);
                    _failpoints.Hit(Failpoint.AfterRuntimeCommandExecution);
                    continue;
                }

                var commits = new List<CommitRuntimeCommand> { firstCommit };
                await Task.Yield();
                while (commits.Count < 64 &&
                       _commands.Reader.TryPeek(out var next) &&
                       next is CommitRuntimeCommand &&
                       _commands.Reader.TryRead(out var admitted))
                {
                    commits.Add((CommitRuntimeCommand)admitted);
                }

                try
                {
                    if (commitCoalescer.CanAttempt(commits))
                    {
                        await ExecuteCoalescedCommitsAsync(_state, commits, commitCoalescer)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var commit in commits)
                        {
                            await commit.ExecuteAsync(_state).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    foreach (var commit in commits)
                    {
                        commit.DisposePayload();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_loopCancellation.IsCancellationRequested)
        {
        }
    }

    async ValueTask ExecuteCoalescedCommitsAsync(
        RuntimeState state,
        List<CommitRuntimeCommand> commits,
        CommitCoalescer commitCoalescer)
    {
        var diskStore = _diskStore ??
                        throw new PantsInternalException("A coalesced commit requires persistent storage.");
        var preparedCommands = new List<CommitRuntimeCommand>(commits.Count);
        var stagedBytesByFamily = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        PantsDurability? groupDurability = null;
        Exception? deferredError = null;
        var stopIndex = commits.Count;
        for (var index = 0; index < commits.Count; index++)
        {
            var command = commits[index];
            if (!commitCoalescer.TryStage(
                    state,
                    command,
                    groupDurability,
                    stagedBytesByFamily))
            {
                stopIndex = index;
                break;
            }

            groupDurability ??= command.WriteOptions.Durability;

            try
            {
                await PrepareCommitAsync(
                        state,
                        command.Payload,
                        command.WriteOptions.Durability,
                        command.Deadline)
                    .ConfigureAwait(false);
                preparedCommands.Add(command);
            }
            catch (Exception exception)
            {
                deferredError = exception;
                stopIndex = index;
                break;
            }
        }

        if (preparedCommands.Count == 0)
        {
            await ExecuteRemainingDrainedCommitsAsync(
                    state,
                    commits,
                    stopIndex,
                    deferredError)
                .ConfigureAwait(false);
            return;
        }

        var durability = groupDurability ??
                         throw new PantsInternalException("A coalesced commit group has no durability policy.");
        var writtenWalSegmentId = diskStore.CurrentWalSegmentId;
        IReadOnlyList<PreparedCoalescedCommit> prepared;
        try
        {
            prepared = CommitCoalescer.CreatePreparedCommits(state, preparedCommands);
            await commitCoalescer.AppendAsync(
                    state,
                    prepared,
                    durability,
                    Failpoint.BeforeCoalescedWalDurabilityBoundary)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (RuntimeExceptionMapper.IsNoSpace(exception))
            {
                state.RecordNoSpaceEvent();
            }

            foreach (var command in preparedCommands)
            {
                command.Fail(state, exception, false);
            }

            if (exception is WalCommitGroupRollbackException)
            {
                RecordPostDurabilityFailure(state, exception);
                FailRemainingDrainedCommits(state, commits, stopIndex, exception);
                return;
            }

            await ExecuteRemainingDrainedCommitsAsync(
                    state,
                    commits,
                    stopIndex,
                    deferredError)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            if (durability == PantsDurability.Sync)
            {
                _failpoints.Hit(Failpoint.AfterCoalescedWalDurabilityBoundary);
            }
        }
        catch (Exception exception)
        {
            RecordPostDurabilityFailure(state, exception);
        }

        try
        {
            _failpoints.Hit(Failpoint.BeforeCoalescedCommitApply);
            commitCoalescer.Apply(state, prepared);
            foreach (var commit in prepared)
            {
                if (_cloudPersistence is not null)
                {
                    TrackCloudMemtableWrites(commit.Command.Payload, writtenWalSegmentId);
                }

                _telemetry.RecordTransactionCommit();
            }

            PublishSnapshot(state);
        }
        catch (Exception exception)
        {
            RecordPostDurabilityFailure(state, exception);
            foreach (var commit in prepared)
            {
                commit.Command.Fail(state, exception, false);
            }

            await ExecuteRemainingDrainedCommitsAsync(
                    state,
                    commits,
                    stopIndex,
                    deferredError)
                .ConfigureAwait(false);
            return;
        }

        foreach (var commit in prepared)
        {
            commit.Command.Complete(IsCommitWriteStalled(state, commit.Families));
        }

        await RunCoalescedCommitMaintenanceAsync(state, diskStore, prepared)
            .ConfigureAwait(false);
        await ExecuteRemainingDrainedCommitsAsync(
                state,
                commits,
                stopIndex,
                deferredError)
            .ConfigureAwait(false);
    }

    void FailRemainingDrainedCommits(
        RuntimeState state,
        List<CommitRuntimeCommand> commits,
        int startIndex,
        Exception failure)
    {
        for (var index = startIndex; index < commits.Count; index++)
        {
            var command = commits[index];
            DiscardUnpreparedCommit(state, command.Payload);
            command.Fail(state, failure);
        }
    }

    static async ValueTask ExecuteRemainingDrainedCommitsAsync(
        RuntimeState state,
        List<CommitRuntimeCommand> commits,
        int stopIndex,
        Exception? deferredError)
    {
        var nextIndex = stopIndex;
        if (stopIndex < commits.Count)
        {
            var stopped = commits[stopIndex];
            if (deferredError is not null)
            {
                stopped.Fail(state, deferredError);
            }
            else
            {
                await stopped.ExecuteAsync(state).ConfigureAwait(false);
            }

            nextIndex++;
        }

        for (var index = nextIndex; index < commits.Count; index++)
        {
            await commits[index].ExecuteAsync(state).ConfigureAwait(false);
        }
    }

    async ValueTask RunCoalescedCommitMaintenanceAsync(
        RuntimeState state,
        LocalDiskStore diskStore,
        IReadOnlyList<PreparedCoalescedCommit> prepared)
    {
        try
        {
            foreach (var commit in prepared)
            {
                await FlushAtConfiguredThresholdAsync(state, commit.Families)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RecordPostDurabilityFailure(state, exception);
        }

        try
        {
            await RotateLocalWalAtConfiguredThresholdAsync(state, diskStore).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordPostDurabilityFailure(state, exception);
        }

        if (_cloudPersistence is not null &&
            prepared[0].Command.WriteOptions.Durability == PantsDurability.CloudAsync)
        {
            foreach (var commit in prepared)
            {
                RecordCloudWalWrite(
                    _cloudWalSealController ??
                    throw new PantsInternalException("CloudAsync has no WAL seal controller."),
                    commit.Command.Payload);
            }

            ScheduleCloudAsyncWalMaintenance(diskStore);
        }
    }

    void ScheduleCloudAsyncWalMaintenance(LocalDiskStore diskStore)
    {
        var controller = _cloudWalSealController ??
                         throw new PantsInternalException("CloudAsync has no WAL seal controller.");
        if (controller.ShouldSeal(diskStore.ActiveWalBytes))
        {
            ScheduleCloudWalSealDeadline(TimeSpan.Zero);
            return;
        }

        ScheduleCloudWalSealDeadline();
        ScheduleCloudWalBacklogDrain();
    }

    void RecordPostDurabilityFailure(RuntimeState state, Exception exception)
    {
        if (RuntimeExceptionMapper.ToPublicException(exception) is PantsNoSpaceException)
        {
            state.RecordNoSpaceEvent();
            _telemetry.RecordWriteStallNoSpace();
        }

        Volatile.Write(ref _persistenceAnomaly, 1);
        MarkPersistenceAnomaly(state);
    }

    async ValueTask<bool> ExecuteCommitAsync(
        RuntimeState state,
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        OperationDeadline deadline)
    {
        EnsureCloudWriteAuthorityValid();
        await PrepareCommitAsync(state, payload, writeOptions.Durability, deadline)
            .ConfigureAwait(false);
        if (payload.Operations.Count != 0)
        {
            ulong? writtenWalSegmentId = null;
            if (_diskStore is null)
            {
                state.Sequence++;
            }
            else
            {
                writtenWalSegmentId = _diskStore.CurrentWalSegmentId;
                var automaticallyFlushed = await PersistCommitAsync(
                        state,
                        payload,
                        writeOptions.Durability,
                        deadline)
                    .ConfigureAwait(false);
                ApplyCommittedOperations(state, payload, state.Sequence);
                TrackCloudMemtableWrites(payload, writtenWalSegmentId.Value);
                if (automaticallyFlushed)
                {
                    state.UnflushedFamilies.Clear();
                    ClearMemtableAccounting(state);
                    _cloudMemtableSegments?.Reinitialize([], _diskStore.CurrentWalSegmentId);
                }
            }

            if (!writtenWalSegmentId.HasValue)
            {
                ApplyCommittedOperations(state, payload, state.Sequence);
            }

            if (_cloudPersistence is not null &&
                writeOptions.Durability == PantsDurability.CloudStrict)
            {
                PublishSnapshot(state);
                try
                {
                    await CompleteCloudStrictCommitAsync(payload, deadline).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordPostDurabilityFailure(state, exception);
                    PublishSnapshot(state);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
            }

            try
            {
                await FlushAtWalRecordThresholdAsync(state).ConfigureAwait(false);
                await FlushAtConfiguredThresholdAsync(state, payload).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                HasAuthoritativeLocalWalWrite(writeOptions.Durability))
            {
                RecordPostDurabilityFailure(state, exception);
            }
        }
        else if (payload.Mode == PantsTransactionMode.ReadWrite &&
                 writeOptions.Durability == PantsDurability.Sync &&
                 _diskStore is not null)
        {
            _ = await _walRuntime.FlushDurabilityBoundaryAsync().ConfigureAwait(false);
        }

        PublishSnapshot(state);
        _telemetry.RecordTransactionCommit();
        return IsCommitWriteStalled(state, payload);
    }

    async ValueTask PrepareCommitAsync(
        RuntimeState state,
        CommitPayload payload,
        PantsDurability durability,
        OperationDeadline deadline)
    {
        ThrowIfShuttingDown(state);
        ThrowIfVerificationInProgress();
        if (RequiresWriteAdmission(payload))
        {
            ThrowIfCloudWalUploadWriteStalled();
        }

        if (!state.ActiveTransactions.Remove(payload.TransactionId))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Transaction {payload.TransactionId} is not active.");
        }

        _telemetry.RecordSnapshotUnregister();
        if (_diskStore is not null)
        {
            DeferObsoleteFileCollectionUntilSnapshotsRelease(state);
            if (payload.Operations.Count != 0)
            {
                _hybridCache?.EnsureWriteAdmitted(_diskStore, state);
            }

            var storageChanged = false;
            await _garbageCollectionWorker
                .ExecuteAsync(() =>
                    storageChanged = _diskStore.CollectObsoleteFiles(state))
                .ConfigureAwait(false);
            var requiresCloudMaintenance = _cloudPersistence is not null &&
                                           (storageChanged ||
                                            _cloudPersistence.HasPersistenceAnomaly ||
                                            _hybridCache?.RequiresEviction(_diskStore) == true);
            if (durability == PantsDurability.CloudAsync)
            {
                if (requiresCloudMaintenance)
                {
                    _cloudMaintenanceScheduler.Signal();
                }
            }
            else if (requiresCloudMaintenance)
            {
                await MirrorCloudStorageAsync(deadline).ConfigureAwait(false);
            }
            else if (_cloudPersistence is not null && payload.Operations.Count != 0)
            {
                await ExecuteCloudWithinDeadlineAsync(
                        deadline,
                        _cloudPersistence.ValidateWriteAuthorityAsync,
                        false)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            await CommitValidator.ValidateAsync(
                state,
                payload,
                _diskStore,
                _scanMemoryBudget,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PantsWriteConflictException exception)
        {
            _telemetry.RecordWriteConflict(exception.IsRangeConflict);
            throw;
        }

        if (payload.Operations.Count != 0)
        {
            await RelieveWritePressureAsync(state, payload, deadline).ConfigureAwait(false);
        }
    }

    static void ApplyCommittedOperations(
        RuntimeState state,
        CommitPayload payload,
        long sequence)
    {
        ApplyOperations(state, payload, sequence);
        RecordMemtableBytes(state, payload);
        foreach (var family in GetOperationFamilies(payload))
        {
            state.UnflushedFamilies.Add(family);
        }
    }

    void DiscardUnpreparedCommit(RuntimeState state, CommitPayload payload)
    {
        if (state.ActiveTransactions.Remove(payload.TransactionId))
        {
            _telemetry.RecordSnapshotUnregister();
        }
    }

    async ValueTask<bool> PersistCommitAsync(
        RuntimeState state,
        CommitPayload payload,
        PantsDurability durability,
        OperationDeadline deadline)
    {
        var diskStore = _diskStore ??
                        throw new PantsInternalException("Persistent commit has no disk store.");
        var result = await _walRuntime.AppendCommitAsync(
                payload,
                state,
                durability is PantsDurability.CloudAsync or PantsDurability.CloudStrict
                    ? PantsDurability.Buffered
                    : durability)
            .ConfigureAwait(false);
        if (result.PostDurabilityFailure is not null)
        {
            RecordPostDurabilityFailure(state, result.PostDurabilityFailure);
        }

        var automaticallyFlushed = false;
        if (!UsesBackgroundImmutableFlushes &&
            _options.FlushAfterWalRecords > 0 &&
            diskStore.WalRecords >= _options.FlushAfterWalRecords)
        {
            await _flushRuntime.FlushAsync(state).ConfigureAwait(false);
            await MirrorCloudStorageAsync(deadline).ConfigureAwait(false);
            await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
            automaticallyFlushed = true;
        }

        try
        {
            await RotateLocalWalAtConfiguredThresholdAsync(state, diskStore).ConfigureAwait(false);
        }
        catch (Exception exception) when (HasAuthoritativeLocalWalWrite(durability))
        {
            RecordPostDurabilityFailure(state, exception);
        }

        if (_cloudPersistence is not null && durability == PantsDurability.CloudAsync)
        {
            var controller = _cloudWalSealController ??
                             throw new PantsInternalException("CloudAsync has no WAL seal controller.");
            RecordCloudWalWrite(controller, payload);
            if (controller.ShouldSeal(diskStore.ActiveWalBytes))
            {
                try
                {
                    await SealCloudAsyncWalAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordPostDurabilityFailure(state, exception);
                    if (IsRetryableCloudWalSealFailure(exception))
                    {
                        ScheduleCloudWalSealDeadline(TimeSpan.Zero);
                    }
                }
            }
            else
            {
                ScheduleCloudWalSealDeadline();
                ScheduleCloudWalBacklogDrain();
            }
        }

        return automaticallyFlushed;
    }

    async ValueTask CompleteCloudStrictCommitAsync(
        CommitPayload payload,
        OperationDeadline deadline)
    {
        var controller = _cloudWalSealController ??
                         throw new PantsInternalException("CloudStrict has no WAL seal controller.");
        RecordCloudWalWrite(controller, payload);

        CloudWalSealResult seal;
        try
        {
            seal = await SealWalForCloudAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (IsRetryableCloudWalSealFailure(exception))
            {
                ScheduleCloudWalSealDeadline(TimeSpan.Zero);
            }

            throw;
        }

        if (seal.Segment is not null)
        {
            controller.RecordSeal();
            CancelCloudWalSealDeadline();
        }

        if (seal.PostRotationFailure is not null)
        {
            ScheduleCloudWalBacklogDrain();
            ExceptionDispatchInfo.Capture(seal.PostRotationFailure).Throw();
        }

        try
        {
            await ExecuteCloudWithinDeadlineAsync(
                    deadline,
                    DrainCloudWalBacklogWithFailureTrackingAsync,
                    true)
                .ConfigureAwait(false);
        }
        catch (PantsException exception) when (IsRetryableCloudWalSealFailure(exception))
        {
            ScheduleCloudWalBacklogDrain();
            throw new PantsIOException(
                "Cloud-strict WAL publication outcome is indeterminate; local recovery " +
                "evidence is retained for retry.",
                exception);
        }
    }

    async ValueTask SealCloudAsyncWalAsync()
    {
        EnsureCloudWriteAuthorityValid();
        var seal = await SealWalForCloudAsync().ConfigureAwait(false);
        if (seal.Segment is null)
        {
            return;
        }

        _cloudWalSealController?.RecordSeal();
        CancelCloudWalSealDeadline();
        ScheduleCloudWalBacklogDrain();
    }

    async ValueTask<CloudWalSealResult> SealWalForCloudAsync()
    {
        var started = Stopwatch.GetTimestamp();
        SealedWalSegment? segment;
        Exception? postRotationFailure = null;
        try
        {
            segment = await _walRuntime.SealCloudWalAsync().ConfigureAwait(false);
        }
        catch (WalCloudSealCompletedException completed)
        {
            segment = completed.Segment;
            postRotationFailure = completed.InnerException ?? completed;
        }

        if (segment is not null)
        {
            _cloudWalUploads.Admit(segment);
            await _walRuntime.CompleteCloudWalSealAsync(segment).ConfigureAwait(false);
            _telemetry.RecordCloudAsyncWalSegmentSealed(
                segment.SegmentId,
                segment.Bytes.LongLength,
                Stopwatch.GetElapsedTime(started));
        }

        return new CloudWalSealResult(segment, postRotationFailure);
    }

    void ScheduleCloudWalBacklogDrain() => _cloudWalDrainScheduler.Signal();

    void ScheduleCloudWalSealDeadline(TimeSpan? requestedDelay = null)
    {
        var delay = requestedDelay ?? _cloudWalSealController?.RemainingDelay;
        if (!delay.HasValue || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loopCancellation.Token);
        Task deadlineTask;
        lock (_cloudWalSealDeadlineGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cancellation.Dispose();
                return;
            }

            _cloudWalSealDeadlineCancellation?.Cancel();
            _cloudWalSealDeadlineCancellation = cancellation;
            deadlineTask = RunCloudWalSealDeadlineAsync(delay.Value, cancellation);
            _cloudWalSealDeadlineTasks.Add(deadlineTask);
        }

        _ = ObserveCloudWalSealDeadlineAsync(deadlineTask, cancellation);
    }

    async Task ObserveCloudWalSealDeadlineAsync(
        Task deadlineTask,
        CancellationTokenSource cancellation)
    {
        try
        {
            await deadlineTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
        finally
        {
            lock (_cloudWalSealDeadlineGate)
            {
                if (ReferenceEquals(_cloudWalSealDeadlineCancellation, cancellation))
                {
                    _cloudWalSealDeadlineCancellation = null;
                }

                _cloudWalSealDeadlineTasks.Remove(deadlineTask);
            }

            cancellation.Dispose();
        }
    }

    async Task RunCloudWalSealDeadlineAsync(
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        var retryDelay = InitialCloudWalSealRetryDelay;
        try
        {
            await Task.Delay(delay, _runtimeTimeProvider, cancellation.Token)
                .ConfigureAwait(false);
            while (true)
            {
                try
                {
                    _ = await SendAsync(
                        async state =>
                        {
                            if (state.IsShuttingDown)
                            {
                                return true;
                            }

                            if (_verificationBarrier is not null)
                            {
                                _cloudWalSealPending = true;
                                return true;
                            }

                            if (_diskStore is not null &&
                                _cloudWalSealController?.PendingWrites > 0)
                            {
                                await SealCloudAsyncWalAsync().ConfigureAwait(false);
                                await FlushCloudSegmentGapAsync(state).ConfigureAwait(false);
                                PublishSnapshot(state);
                            }

                            return true;
                        },
                        cancellation.Token).ConfigureAwait(false);
                    return;
                }
                catch (PantsException) when (
                    !cancellation.IsCancellationRequested &&
                    Volatile.Read(ref _disposed) == 0)
                {
                    await Task.Delay(
                            retryDelay,
                            _runtimeTimeProvider,
                            cancellation.Token)
                        .ConfigureAwait(false);
                    retryDelay = NextCloudWalSealRetryDelay(retryDelay);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (PantsException)
        {
            // The locally durable WAL remains available for the next commit,
            // shutdown, or recovery retry.
        }
    }

    static void RecordCloudWalWrite(
        CloudWalSealController controller,
        CommitPayload payload)
    {
        if (payload.Operations.Count == 0)
        {
            return;
        }

        controller.RecordWrite(payload.IsSpilled
            ? checked((int)payload.Operations.Count + 2)
            : 1);
    }

    static bool IsRetryableCloudWalSealFailure(Exception exception) =>
        RuntimeExceptionMapper.ToPublicException(exception) is PantsIOException or
            PantsTimeoutException;

    static TimeSpan NextCloudWalSealRetryDelay(TimeSpan current) =>
        current >= MaximumCloudWalSealRetryDelay
            ? MaximumCloudWalSealRetryDelay
            : TimeSpan.FromTicks(Math.Min(
                current.Ticks * 2,
                MaximumCloudWalSealRetryDelay.Ticks));

    void CancelCloudWalSealDeadline()
    {
        lock (_cloudWalSealDeadlineGate)
        {
            _cloudWalSealDeadlineCancellation?.Cancel();
            _cloudWalSealDeadlineCancellation = null;
        }
    }

    Task[] CancelCloudWalSealDeadlinesForDisposal()
    {
        lock (_cloudWalSealDeadlineGate)
        {
            _cloudWalSealDeadlineCancellation?.Cancel();
            _cloudWalSealDeadlineCancellation = null;
            return [.. _cloudWalSealDeadlineTasks];
        }
    }

    async ValueTask PublishCloudWalBatchAsync(
        SealedWalSegment[] segments,
        CancellationToken cancellationToken)
    {
        if (segments.Length == 0)
        {
            return;
        }

        EnsureCloudWriteAuthorityValid();
        var persistence = _cloudPersistence ??
                          throw new PantsInternalException("Cloud WAL publication has no persistence backend.");
        var started = Stopwatch.GetTimestamp();
        foreach (var _ in segments)
        {
            _telemetry.RecordCloudAsyncWalUploadStarted();
        }

        try
        {
            _failpoints.Hit(Failpoint.BeforeCloudWalUpload);
            await persistence.PublishWalBatchAsync(segments, cancellationToken).ConfigureAwait(false);
            _failpoints.Hit(Failpoint.AfterCloudWalUpload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            foreach (var _ in segments)
            {
                _telemetry.RecordCloudAsyncWalUploadFailed();
            }

            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        foreach (var segment in segments)
        {
            _telemetry.RecordCloudAsyncWalUploadCompleted(elapsed);
            EnsureCloudWriteAuthorityValid();
            Volatile.Write(
                ref _walCloudDurableSequence,
                Math.Max(
                    Volatile.Read(ref _walCloudDurableSequence),
                    checked((long)segment.MaximumSequence)));
            if (_diskStore is not null)
            {
                if (_workersStarted)
                {
                    await _walRuntime.DeleteCloudDurableSegmentAsync(segment, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _diskStore.DeleteCloudDurableWalSegment(segment);
                }
            }

            _telemetry.RecordCloudAsyncWalAcknowledged(segment.SegmentId);
            _cloudWalUploads.Complete(segment);
        }
    }

    async ValueTask DrainCloudWalBacklogAsync(CancellationToken cancellationToken)
    {
        EnsureCloudWriteAuthorityValid();
        if (_diskStore is null)
        {
            return;
        }

        IReadOnlyList<SealedWalSegment> segments;
        if (_workersStarted)
        {
            segments = await _walRuntime.GetCloudBacklogAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            segments = _diskStore.GetSealedWalSegmentsForCloudPublication();
        }

        foreach (var epochBatch in segments.GroupBy(static segment => segment.WriterEpoch))
        {
            var batch = epochBatch.ToArray();
            foreach (var segment in batch)
            {
                _cloudWalUploads.Admit(segment);
            }

            EnsureCloudWriteAuthorityValid();
            await PublishCloudWalBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask DrainCloudWalBacklogWithFailureTrackingAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await DrainCloudWalBacklogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            throw RuntimeExceptionMapper.ToPublicException(exception, cancellationToken);
        }
    }

    async ValueTask TryDrainCloudWalBacklogOnShutdownAsync(RuntimeState state)
    {
        try
        {
            await _cloudWorker.ExecuteAsync(DrainCloudWalBacklogWithFailureTrackingAsync)
                .ConfigureAwait(false);
        }
        catch (PantsException)
        {
            // CloudAsync acknowledges at the local WAL boundary. A failed
            // shutdown upload must retain that WAL for startup recovery.
            MarkPersistenceAnomaly(state);
        }
    }

    async ValueTask TryMirrorCloudStorageOnShutdownAsync(RuntimeState state)
    {
        try
        {
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
        catch (PantsException)
        {
            // The local manifest and WAL remain authoritative enough for a
            // same-cache retry; remote recovery must ignore unpublished data.
            MarkPersistenceAnomaly(state);
        }
    }

    async ValueTask RotateLocalWalAtConfiguredThresholdAsync(
        RuntimeState state,
        LocalDiskStore diskStore)
    {
        if (_options.Storage is not PantsStorageConfiguration.Local ||
            (UsesBackgroundImmutableFlushes && state.ImmutableMemtableFlushes.Count != 0) ||
            diskStore.ActiveWalBytes < _options.WalBufferSizeBytes)
        {
            return;
        }

        _ = await _walRuntime.RotateLocalWalAsync().ConfigureAwait(false);
    }

    bool HasAuthoritativeLocalWalWrite(PantsDurability durability) =>
        _options.Storage is PantsStorageConfiguration.Local &&
        durability is PantsDurability.Sync or PantsDurability.Buffered;

    static void ApplyOperations(RuntimeState state, CommitPayload payload, long sequence) =>
        payload.Operations.ForEach(operation => ApplyOperation(state, operation, sequence));

    static void ApplyOperation(
        RuntimeState state,
        TransactionIntentOperation operation,
        long sequence)
    {
        var family = GetFamily(state, operation.Family);
        switch (operation.Kind)
        {
            case CommitOperationKind.Put:
                state.FamilyData[operation.Family] = family.SetItem(operation.Key.ToArray(), new CellState(
                    operation.Value?.ToArray(),
                    sequence,
                    operation.ExpiryUtc));
                break;
            case CommitOperationKind.Delete:
                state.FamilyData[operation.Family] =
                    family.SetItem(operation.Key.ToArray(), new CellState(null, sequence, null));
                break;
            case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                state.RangeTombstones[operation.Family] = state.RangeTombstones[operation.Family].Add(
                    new CommittedRangeTombstone(
                        operation.Key.ToArray(),
                        operation.EndExclusive.ToArray(),
                        sequence));
                foreach (var key in family.Keys
                             .Where(key => IsInRange(key, operation.Key, operation.EndExclusive))
                             .ToArray())
                {
                    family = family.SetItem(key, new CellState(null, sequence, null));
                }

                state.FamilyData[operation.Family] = family;

                break;
            default:
                throw PantsException.Create(
                    PantsErrorCode.Internal,
                    $"Unsupported transaction operation '{operation.Kind}'.");
        }
    }

    static void ValidateActiveFamily(RuntimeState state, ColumnFamilyIdentity identity)
    {
        if (!IsActiveFamily(state, identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

    static bool IsActiveFamily(RuntimeState state, ColumnFamilyIdentity identity) =>
        state.ActiveFamilyVersions.TryGetValue(identity.Name, out var activeGeneration) &&
        activeGeneration == identity.Generation &&
        state.FamilyData.ContainsKey(identity);

    static ImmutableSortedDictionary<byte[], CellState> GetFamily(
        RuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.FamilyData.TryGetValue(identity, out var family)
            ? family
            : throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is unavailable.");

    static bool IsInRange(byte[] key, byte[] start, byte[] end) =>
        ByteArrayComparer.Instance.Compare(key, start) >= 0 &&
        ByteArrayComparer.Instance.Compare(key, end) < 0;

    async ValueTask FlushAtConfiguredThresholdAsync(
        RuntimeState state,
        CommitPayload payload) =>
        await FlushAtConfiguredThresholdAsync(state, GetOperationFamilies(payload))
            .ConfigureAwait(false);

    async ValueTask FlushAtConfiguredThresholdAsync(
        RuntimeState state,
        IReadOnlyList<ColumnFamilyIdentity> operationFamilies)
    {
        if (_diskStore is null)
        {
            return;
        }

        if (operationFamilies.Any(family =>
                state.ActiveMemtableBytes.GetValueOrDefault(family) >=
                _options.MemtableFlushThresholdBytes))
        {
            if (UsesBackgroundImmutableFlushes)
            {
                foreach (var family in operationFamilies)
                {
                    if (state.ActiveMemtableBytes.GetValueOrDefault(family) >=
                        _options.MemtableFlushThresholdBytes)
                    {
                        await FreezeAndScheduleFlushAsync(
                                state,
                                family,
                                false)
                            .ConfigureAwait(false);
                    }
                }

                return;
            }

            var started = Stopwatch.GetTimestamp();
            await _flushRuntime.FlushAsync(state).ConfigureAwait(false);
            _telemetry.RecordFlush(Stopwatch.GetElapsedTime(started));
            await MirrorCloudStorageAsync().ConfigureAwait(false);
            state.UnflushedFamilies.Clear();
            ClearMemtableAccounting(state);
            _cloudMemtableSegments?.Reinitialize([], _diskStore.CurrentWalSegmentId);
            await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
            return;
        }

        await FlushCloudSegmentGapAsync(state).ConfigureAwait(false);
    }

    async ValueTask FlushAtWalRecordThresholdAsync(RuntimeState state)
    {
        if (!UsesBackgroundImmutableFlushes ||
            _options.FlushAfterWalRecords <= 0 ||
            _diskStore!.WalRecords < _options.FlushAfterWalRecords)
        {
            return;
        }

        _failpoints.Hit(Failpoint.BeforeWalRecordThresholdFlush);

        foreach (var family in state.ActiveMemtableBytes
                     .Where(static pair => pair.Value > 0)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _ = await FreezeAndScheduleFlushAsync(
                    state,
                    family,
                    false)
                .ConfigureAwait(false);
        }
    }

    async Task ScheduleRecoveredMemtableFlushesAsync()
    {
        try
        {
            _ = await SendAsync(
                async state =>
                {
                    if (state.IsShuttingDown)
                    {
                        _recoveredMemtableFlushPending = false;
                        return true;
                    }

                    if (_verificationBarrier is not null)
                    {
                        _recoveredMemtableFlushPending = true;
                        return true;
                    }

                    _recoveredMemtableFlushPending = false;
                    await FreezeRecoveredMemtablesAsync(state).ConfigureAwait(false);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PantsAbortedException)
        {
        }
        catch (Exception)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    async ValueTask FreezeRecoveredMemtablesAsync(RuntimeState state)
    {
        foreach (var family in state.ActiveMemtableBytes
                     .Where(pair => pair.Value >= _options.MemtableFlushThresholdBytes)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _ = await FreezeAndScheduleFlushAsync(
                    state,
                    family,
                    false)
                .ConfigureAwait(false);
        }
    }

    async ValueTask FlushImmutableMemtableAsync(
        ColumnFamilyIdentity identity,
        CancellationToken cancellationToken)
    {
        var attempts = await SendAsync<IReadOnlyList<Task<Exception?>>>(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                EnsureCloudWriteAuthorityValid();
                ValidateActiveFamily(state, identity);
                _ = await FreezeAndScheduleFlushAsync(state, identity).ConfigureAwait(false);
                await ScheduleNextImmutableFlushAttemptAsync(state, true)
                    .ConfigureAwait(false);
                return state.ImmutableMemtableFlushes.Values
                    .Where(flush => flush.Frozen.ColumnFamily == identity)
                    .Select(static flush => flush.AttemptTask)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await ImmutableFlushPipeline.AwaitAttemptsAsync(attempts, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask WaitForColumnFamilyFlushAsync(
        ColumnFamilyIdentity identity,
        bool retryFailures,
        CancellationToken cancellationToken)
    {
        var attempts = await SendAsync<IReadOnlyList<Task<Exception?>>>(
            async state =>
            {
                ValidateActiveFamily(state, identity);
                var flushes = state.ImmutableMemtableFlushes.Values
                    .Where(flush => flush.Frozen.ColumnFamily == identity)
                    .ToArray();
                if (retryFailures)
                {
                    await ScheduleNextImmutableFlushAttemptAsync(state, true)
                        .ConfigureAwait(false);
                }

                return flushes.Select(static flush => flush.AttemptTask).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await ImmutableFlushPipeline.AwaitAttemptsAsync(attempts, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask<ImmutableMemtableFlush?> FreezeAndScheduleFlushAsync(
        RuntimeState state,
        ColumnFamilyIdentity identity,
        bool rejectWhenQueueFull = true)
    {
        if (state.IsShuttingDown)
        {
            return null;
        }

        var diskStore = _diskStore ??
                        throw new PantsInternalException("An immutable flush requires local storage.");
        var sizeBytes = state.ActiveMemtableBytes.GetValueOrDefault(identity);
        if (sizeBytes == 0)
        {
            return null;
        }

        if (MemtableWritePressure.IsQueueFull(state, identity))
        {
            _telemetry.RecordWriteStallMemory();
            if (!rejectWhenQueueFull)
            {
                return null;
            }

            throw new PantsWriteStallException(
                $"The immutable memtable queue is full for column family '{identity.Name}'.");
        }

        var frozen = diskStore.FreezeMemtable(
            identity,
            sizeBytes,
            checked((ulong)state.Sequence));
        if (frozen is null)
        {
            return null;
        }

        state.ActiveMemtableBytes[identity] = 0;
        var flush = new ImmutableMemtableFlush(frozen);
        state.ImmutableMemtableFlushes.Add(frozen.Id, flush);
        _telemetry.RecordFlushEnqueued();
        state.SignalWritePressureChanged();
        _backgroundCompactionPending |= _backgroundCompactionEnabled;
        await ScheduleNextImmutableFlushAttemptAsync(state, false)
            .ConfigureAwait(false);
        PublishSnapshot(state);
        return flush;
    }

    async ValueTask ScheduleNextImmutableFlushAttemptAsync(
        RuntimeState state,
        bool retryFailure)
    {
        if (state.IsShuttingDown)
        {
            return;
        }

        await _immutableFlushPipeline
            .ScheduleNextAsync(state.ImmutableMemtableFlushes.Values, retryFailure)
            .ConfigureAwait(false);
    }

    async ValueTask<bool> CompleteImmutableFlushAttemptAsync(
        ImmutableMemtableFlush expected,
        FrozenFlushRuntimeResult? result,
        Exception? failure)
    {
        var shouldRetry = false;
        var shouldRunDeferredCompaction = await SendAsync(
            async state =>
            {
                if (!state.ImmutableMemtableFlushes.TryGetValue(
                        expected.Frozen.Id,
                        out var current) ||
                    !ReferenceEquals(current, expected))
                {
                    return false;
                }

                if (result is not null)
                {
                    current.PublicationPlan ??= result.PublicationPlan;
                    current.PersistenceAnomaly |= result.PersistenceAnomaly;
                }

                if (failure is not null)
                {
                    if (failure is PantsNoSpaceException)
                    {
                        state.RecordNoSpaceEvent();
                        _telemetry.RecordWriteStallNoSpace();
                    }

                    _telemetry.RecordFlushFailure();
                    current.CompleteAttempt(failure);
                    if (failure is PantsCorruptionException)
                    {
                        MarkPersistenceAnomaly(state);
                    }

                    shouldRetry = failure is not PantsCorruptionException && !state.IsShuttingDown;
                    state.SignalWritePressureChanged();
                    PublishSnapshot(state);
                    return false;
                }

                if (current.PersistenceAnomaly)
                {
                    Volatile.Write(ref _persistenceAnomaly, 1);
                    MarkPersistenceAnomaly(state);
                }

                state.ImmutableMemtableFlushes.Remove(current.Frozen.Id);
                state.ReleaseFlushedGeneration(current.Frozen);
                state.SignalWritePressureChanged();
                var identity = current.Frozen.ColumnFamily;
                if (!state.IsShuttingDown &&
                    state.ActiveMemtableBytes.GetValueOrDefault(identity) >=
                    _options.MemtableFlushThresholdBytes)
                {
                    _ = await FreezeAndScheduleFlushAsync(
                            state,
                            identity,
                            false)
                        .ConfigureAwait(false);
                }

                if (state.ActiveMemtableBytes.GetValueOrDefault(identity) == 0 &&
                    !state.ImmutableMemtableFlushes.Values.Any(flush =>
                        flush.Frozen.ColumnFamily == identity))
                {
                    state.UnflushedFamilies.Remove(identity);
                }

                PublishSnapshot(state);
                current.CompleteAttempt(null);
                if (!state.IsShuttingDown)
                {
                    await ScheduleNextImmutableFlushAttemptAsync(
                            state,
                            false)
                        .ConfigureAwait(false);
                }

                return !state.IsShuttingDown &&
                       state.ImmutableMemtableFlushes.Count == 0 &&
                       (_backgroundCompactionPending ||
                        _readAmplificationCompactionPending);
            },
            CancellationToken.None).ConfigureAwait(false);
        if (shouldRunDeferredCompaction)
        {
            ScheduleDeferredCompaction();
        }

        return shouldRetry;
    }

    async ValueTask RetryImmutableFlushAttemptAsync(
        ImmutableMemtableFlush expected,
        int failedAttempt)
    {
        _ = await SendAsync(
            async state =>
            {
                if (state.IsShuttingDown || _verificationBarrier is not null ||
                    !state.ImmutableMemtableFlushes.TryGetValue(
                        expected.Frozen.Id,
                        out var current) ||
                    !ReferenceEquals(current, expected) ||
                    current.Attempts != failedAttempt ||
                    !current.HasFailed ||
                    current.IsRunning)
                {
                    return true;
                }

                await ScheduleNextImmutableFlushAttemptAsync(
                        state,
                        true)
                    .ConfigureAwait(false);
                return true;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    async ValueTask DrainImmutableFlushesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var attempts = await SendAsync<IReadOnlyList<Task<Exception?>>>(
                async state =>
                {
                    await ScheduleNextImmutableFlushAttemptAsync(state, true)
                        .ConfigureAwait(false);
                    var flushes = state.ImmutableMemtableFlushes.Values.ToArray();

                    return flushes.Select(static flush => flush.AttemptTask).ToArray();
                },
                cancellationToken).ConfigureAwait(false);
            if (attempts.Count == 0)
            {
                return;
            }

            await ImmutableFlushPipeline.AwaitAttemptsAsync(attempts, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    async ValueTask WaitForImmutableFlushWorkersAsync(CancellationToken cancellationToken)
    {
        var flushes = await SendAsync<IReadOnlyCollection<ImmutableMemtableFlush>>(
            state => ValueTask.FromResult<IReadOnlyCollection<ImmutableMemtableFlush>>(
                state.ImmutableMemtableFlushes.Values.ToArray()),
            cancellationToken).ConfigureAwait(false);
        await ImmutableFlushPipeline.AwaitWorkersAsync(flushes, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask FlushActiveAndImmutableMemtablesAsync(
        CancellationToken cancellationToken)
    {
        var attempts = await SendAsync<IReadOnlyList<Task<Exception?>>>(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                EnsureCloudWriteAuthorityValid();
                foreach (var family in state.ActiveMemtableBytes
                             .Where(static pair => pair.Value > 0)
                             .Select(static pair => pair.Key)
                             .ToArray())
                {
                    _ = await FreezeAndScheduleFlushAsync(state, family).ConfigureAwait(false);
                }

                await ScheduleNextImmutableFlushAttemptAsync(state, true)
                    .ConfigureAwait(false);

                return state.ImmutableMemtableFlushes.Values
                    .Select(static flush => flush.AttemptTask)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await ImmutableFlushPipeline.AwaitAttemptsAsync(attempts, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask FlushCloudSegmentGapAsync(RuntimeState state)
    {
        if (_diskStore is null || _cloudMemtableSegments is null)
        {
            return;
        }

        EnsureCloudWriteAuthorityValid();
        var flushed = false;
        while (true)
        {
            var candidate = _cloudMemtableSegments.SelectFlushCandidate(
                _diskStore.CurrentWalSegmentId,
                checked((ulong)_options.CloudWritePolicy.EventualFlushSegmentGap));
            if (!candidate.HasValue)
            {
                break;
            }

            var identity = candidate.Value;
            var started = Stopwatch.GetTimestamp();
            await _flushRuntime.FlushAsync(state, identity)
                .ConfigureAwait(false);
            _telemetry.RecordFlush(Stopwatch.GetElapsedTime(started));
            await MirrorCloudStorageAsync().ConfigureAwait(false);
            state.UnflushedFamilies.Remove(identity);
            ClearMemtableAccounting(state, identity);
            _cloudMemtableSegments.RecordFlush(identity);
            flushed = true;
        }

        if (flushed)
        {
            await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
        }
    }

    async ValueTask RunBackgroundCompactionAsync(RuntimeState state)
    {
        if (!_backgroundCompactionEnabled || _diskStore is null)
        {
            _backgroundCompactionPending = false;
            return;
        }

        if (state.IsShuttingDown)
        {
            _backgroundCompactionPending = false;
            return;
        }

        if (_verificationBarrier is not null)
        {
            _backgroundCompactionPending = true;
            return;
        }

        if (UsesBackgroundImmutableFlushes && state.ImmutableMemtableFlushes.Count != 0)
        {
            _backgroundCompactionPending = true;
            return;
        }

        _backgroundCompactionPending = false;
        EnsureCloudWriteAuthorityValid();
        await EnsureHybridSstsLocalAsync(
                _diskStore.GetCompactionInputNames(state, false),
                CancellationToken.None)
            .ConfigureAwait(false);
        var result = await _compactionRuntime
            .CompactAsync(
                state,
                false,
                _cloudCompactionOutputPublisher,
                !UsesBackgroundImmutableFlushes,
                EnsureHybridSstsLocalAsync)
            .ConfigureAwait(false);

        if (result.PersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            MarkPersistenceAnomaly(state);
        }

        PublishSnapshot(state);

        if (result.PublicationCount > 0 || _hybridCache is not null)
        {
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }

        if (!UsesBackgroundImmutableFlushes)
        {
            state.UnflushedFamilies.Clear();
            ClearMemtableAccounting(state);
            _cloudMemtableSegments?.Reinitialize(
                [],
                _diskStore.CurrentWalSegmentId);
            PublishSnapshot(state);
        }
    }

    async ValueTask RunReadAmplificationCompactionAsync(RuntimeState state)
    {
        if (!_backgroundCompactionEnabled || _diskStore is null)
        {
            _readAmplificationCompactionPending = false;
            return;
        }

        if (state.IsShuttingDown)
        {
            _readAmplificationCompactionPending = false;
            return;
        }

        if (_verificationBarrier is not null)
        {
            _readAmplificationCompactionPending = true;
            return;
        }

        if (UsesBackgroundImmutableFlushes && state.ImmutableMemtableFlushes.Count != 0)
        {
            _readAmplificationCompactionPending = true;
            return;
        }

        _readAmplificationCompactionPending = false;
        _backgroundCompactionPending = false;
        EnsureCloudWriteAuthorityValid();
        _telemetry.RecordReadAmplificationCompactionTrigger();
        await EnsureHybridSstsLocalAsync(
                _diskStore!.GetCompactionInputNames(state, true),
                CancellationToken.None)
            .ConfigureAwait(false);
        var result = await _compactionRuntime
            .CompactAsync(
                state,
                true,
                _cloudCompactionOutputPublisher,
                !UsesBackgroundImmutableFlushes,
                EnsureHybridSstsLocalAsync)
            .ConfigureAwait(false);
        if (!UsesBackgroundImmutableFlushes)
        {
            state.UnflushedFamilies.Clear();
            ClearMemtableAccounting(state);
            _cloudMemtableSegments?.Reinitialize(
                [],
                _diskStore.CurrentWalSegmentId);
        }

        if (result.PersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            MarkPersistenceAnomaly(state);
        }

        PublishSnapshot(state);

        if (result.PublicationCount > 0 || _hybridCache is not null)
        {
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
    }

    void ScheduleDeferredCompaction()
    {
        if (Interlocked.CompareExchange(ref _deferredCompactionScheduled, 1, 0) == 0)
        {
            _ = RunDeferredCompactionAsync();
        }
    }

    async Task RunDeferredCompactionAsync()
    {
        try
        {
            _failpoints.Hit(Failpoint.BeforeCompactionAdmission);
            _ = await SendAsync(
                async state =>
                {
                    if (state.IsShuttingDown ||
                        (!_backgroundCompactionPending &&
                         !_readAmplificationCompactionPending))
                    {
                        return true;
                    }

                    if (_verificationBarrier is not null)
                    {
                        return true;
                    }

                    if (_readAmplificationCompactionPending)
                    {
                        await RunReadAmplificationCompactionAsync(state).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
                    }

                    PublishSnapshot(state);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PantsAbortedException)
        {
        }
        catch (Exception)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
        finally
        {
            try
            {
                _failpoints.Hit(Failpoint.BeforeDeferredCompactionSignalReset);
            }
            finally
            {
                Volatile.Write(ref _deferredCompactionScheduled, 0);
                await RearmDeferredCompactionAsync().ConfigureAwait(false);
            }
        }
    }

    async ValueTask RearmDeferredCompactionAsync()
    {
        try
        {
            var shouldSchedule = await SendAsync(
                state => ValueTask.FromResult(
                    !state.IsShuttingDown &&
                    _verificationBarrier is null &&
                    state.ImmutableMemtableFlushes.Count == 0 &&
                    (_backgroundCompactionPending ||
                     _readAmplificationCompactionPending)),
                CancellationToken.None).ConfigureAwait(false);
            if (shouldSchedule)
            {
                ScheduleDeferredCompaction();
            }
        }
        catch (PantsAbortedException)
        {
        }
    }

    void ScheduleCloudFlushRetry(ColumnFamilyIdentity identity) =>
        _cloudFlushRetries.Schedule(
            identity,
            cancellationToken => RetryCloudFlushAsync(identity, cancellationToken));

    async ValueTask RetryCloudFlushAsync(
        ColumnFamilyIdentity identity,
        CancellationToken cancellationToken)
    {
        _ = await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                if (_verificationBarrier is not null)
                {
                    throw new PantsTimeoutException(
                        "Cloud flush retry is deferred by online verification.");
                }

                if (!IsActiveFamily(state, identity) || _diskStore is null)
                {
                    return true;
                }

                EnsureCloudWriteAuthorityValid();
                await _flushRuntime.FlushAsync(state, identity, cancellationToken)
                    .ConfigureAwait(false);
                await MirrorCloudStorageAsync().ConfigureAwait(false);
                CompleteCloudFlush(state, identity);
                await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
                PublishSnapshot(state);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    void CompleteCloudFlush(
        RuntimeState state,
        ColumnFamilyIdentity identity)
    {
        state.UnflushedFamilies.Remove(identity);
        ClearMemtableAccounting(state, identity);
        _cloudMemtableSegments?.RecordFlush(identity);
    }

    async ValueTask MirrorCloudStorageAsync(OperationDeadline deadline = default)
    {
        if (_cloudPersistence is null)
        {
            return;
        }

        ThrowIfVerificationInProgress();

        EnsureCloudWriteAuthorityValid();
        deadline = deadline.IsBounded ? deadline : OperationDeadline.Unbounded;
        await ExecuteCloudWithinDeadlineAsync(
                deadline,
                MirrorCloudStorageWithFailureTrackingAsync,
                true)
            .ConfigureAwait(false);
        ApplyPendingPersistenceAnomaly(_state);
    }

    ValueTask ExecuteCloudWithinDeadlineAsync(
        OperationDeadline deadline,
        Func<CancellationToken, ValueTask> operation,
        bool mutation) => mutation
        ? deadline.RunMutationAsync(
            token => _cloudWorker.ExecuteAsync(operation, token),
            CancellationToken.None)
        : deadline.RunAsync(
            token => _cloudWorker.ExecuteAsync(operation, token),
            CancellationToken.None);

    async ValueTask MirrorCloudStorageCoreAsync(CancellationToken cancellationToken)
    {
        var persistence = _cloudPersistence;
        if (persistence is null)
        {
            return;
        }

        EnsureCloudWriteAuthorityValid();
        _telemetry.RecordCloudUploadPending();
        try
        {
            await persistence.MirrorMetadataAndSstsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _telemetry.RecordCloudUploadCompleted();
        }

        if (persistence.HasPersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    async ValueTask MirrorCloudStorageWithFailureTrackingAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await MirrorCloudStorageCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_hybridCache is not null &&
                _diskStore is not null &&
                _cloudPersistence is { HasPersistenceAnomaly: false } persistence)
            {
                await _hybridCache.EvictIfNeededAsync(
                        _diskStore,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            throw RuntimeExceptionMapper.ToPublicException(exception, cancellationToken);
        }
    }

    async ValueTask EnsureHybridSstsLocalAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        if (_hybridCache is null || _diskStore is null || _cloudPersistence is null)
        {
            return;
        }

        if (_verificationBarrier is not null &&
            names.Any(name => !_diskStore.IsSstLocal(name)))
        {
            throw new PantsBusyException(
                "Hybrid cache hydration is deferred by online verification.");
        }

        if (names.Any(name => !_diskStore.IsSstLocal(name)))
        {
            _failpoints.Hit(Failpoint.BeforeHybridSstHydration);
        }

        await HybridCacheManager.EnsureLocalSstsAsync(
                _diskStore,
                names,
                cancellationToken)
            .ConfigureAwait(false);
    }

    static void RecordMemtableBytes(RuntimeState state, CommitPayload payload)
    {
        foreach (var pair in GetOperationBytesByFamily(payload))
        {
            state.ActiveMemtableBytes[pair.Key] = checked(
                state.ActiveMemtableBytes.GetValueOrDefault(pair.Key) + pair.Value);
        }
    }

    void TrackCloudMemtableWrites(CommitPayload payload, ulong walSegmentId)
    {
        if (_cloudMemtableSegments is null)
        {
            return;
        }

        foreach (var family in GetOperationFamilies(payload))
        {
            _cloudMemtableSegments.RecordWrite(family, walSegmentId);
        }
    }

    static long EstimateOperationBytes(TransactionIntentOperation operation) => checked(
        (long)operation.Key.Length +
        (operation.EndExclusive?.Length ?? 0) +
        (operation.Value?.Length ?? 0) +
        64);

    async ValueTask EnsureWriteAdmissionAsync(
        ColumnFamilyIdentity[] families,
        CancellationToken cancellationToken)
    {
        var stalled = await SendAsync(
            state =>
            {
                foreach (var family in families)
                {
                    ValidateActiveFamily(state, family);
                }

                return ValueTask.FromResult(
                    MemtableWritePressure.IsStalled(_options, state, families));
            },
            cancellationToken).ConfigureAwait(false);
        if (!stalled)
        {
            foreach (var family in families)
            {
                _writeStallHints.TryRemove(family, out _);
            }

            return;
        }

        _telemetry.RecordWriteStallMemory();
        throw new PantsWriteStallException(
            "Writes are stalled until the bounded immutable memtable pipeline makes progress.");
    }

    bool IsCommitWriteStalled(RuntimeState state, CommitPayload payload)
    {
        var families = GetCommitFamilies(payload);
        return IsCommitWriteStalled(state, families);
    }

    bool IsCommitWriteStalled(
        RuntimeState state,
        IReadOnlyList<ColumnFamilyIdentity> families) =>
        families.Count != 0 &&
        MemtableWritePressure.IsStalled(_options, state, families);

    static ColumnFamilyIdentity[] GetCommitFamilies(CommitPayload payload) =>
        GetOperationFamilies(payload)
            .Concat(payload.Asserts.Keys)
            .Distinct(ColumnFamilyIdentityComparer.Instance)
            .ToArray();

    static bool RequiresWriteAdmission(CommitPayload payload) =>
        payload.Operations.Count != 0 ||
        payload.Asserts.Values.Any(static assertions => assertions.Count != 0);

    static ColumnFamilyIdentity[] GetOperationFamilies(CommitPayload payload)
    {
        var families = new HashSet<ColumnFamilyIdentity>(ColumnFamilyIdentityComparer.Instance);
        payload.Operations.ForEach(operation => families.Add(operation.Family));
        return [.. families];
    }

    static Dictionary<ColumnFamilyIdentity, long> GetOperationBytesByFamily(
        CommitPayload payload)
    {
        var bytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        payload.Operations.ForEach(operation =>
            bytes[operation.Family] = checked(
                bytes.GetValueOrDefault(operation.Family) + EstimateOperationBytes(operation)));
        return bytes;
    }

    void ClearMemtableAccounting(RuntimeState state)
    {
        foreach (var identity in state.ActiveMemtableBytes.Keys.ToArray())
        {
            if (_diskStore is not null)
            {
                state.ReleasePersistedFamily(identity);
            }

            state.ActiveMemtableBytes[identity] = 0;
        }
    }

    void ClearMemtableAccounting(
        RuntimeState state,
        ColumnFamilyIdentity identity)
    {
        if (_diskStore is not null)
        {
            state.ReleasePersistedFamily(identity);
        }

        state.ActiveMemtableBytes[identity] = 0;
    }

    static PantsStorageLayout EmptyStorageLayout(RuntimeState state) => new(
        state.Health,
        0,
        1,
        [],
        state.ActiveSnapshots
            .Select(snapshot => new PantsSnapshotPin(
                snapshot.SnapshotId,
                snapshot.BeginSequence,
                GetSnapshotAge(state.Clock.UtcNow, snapshot.StartedAtUtc),
                1))
            .ToArray(),
        0,
        [],
        []);

    static TimeSpan GetSnapshotAge(DateTimeOffset now, DateTimeOffset startedAtUtc) =>
        now <= startedAtUtc ? TimeSpan.Zero : now - startedAtUtc;

    bool HasLiveRuntimeMetrics() =>
        Volatile.Read(ref _persistenceAnomaly) == 0 &&
        (Volatile.Read(ref _activeCompactionRequests) != 0 ||
         _compactionRuntime.Outstanding != 0 ||
         _telemetry.PendingCloudUploads != 0 ||
         (_hybridCache?.PendingEvictions ?? 0) != 0);

    void PublishSnapshot(RuntimeState state)
    {
        lock (_directReadAdmissionGate)
        {
            Volatile.Write(ref _currentVersion, state.CreateVersion(VisibleFilesSnapshot()));
            if (_diskStore?.SnapshotPinnedObsoleteFileCount > 0)
            {
                // A direct read can acquire only the newly-published version while this gate is
                // held. Existing readers are already registered in ActiveSnapshotCount, so the
                // store can now collect retired inputs only when no old version can reference them.
                if (state.ActiveSnapshotCount == 0)
                {
                    _ = _diskStore.CollectObsoleteFiles(state);
                }
                else
                {
                    DeferObsoleteFileCollectionUntilSnapshotsRelease(state);
                }
            }
        }

        Volatile.Write(
            ref _publishedRuntimeMetrics,
            _runtimeMetricsSnapshotFactory.RefreshPublished(
                Volatile.Read(ref _publishedRuntimeMetrics),
                state,
                Volatile.Read(ref _walCloudDurableSequence)));
    }

    IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> VisibleFilesSnapshot() =>
        _diskStore?.GetVisibleFilesSnapshot() ?? ImmutableDictionary<uint, ImmutableArray<FileMeta>>.Empty;

    void EnsureCloudLeaseValid() => _cloudLease?.EnsureValid();

    void EnsureCloudWriteAuthorityValid()
    {
        EnsureCloudLeaseValid();
        _cloudDdlCoordinator?.EnsureAuthorityResolved();
    }

    static void MarkPersistenceAnomaly(RuntimeState state)
    {
        if (state.Health == PantsEngineHealth.Healthy)
        {
            state.Health = PantsEngineHealth.Degraded;
        }
    }

    void ApplyPendingPersistenceAnomaly(RuntimeState state)
    {
        if (_cloudPersistence?.HasPersistenceAnomaly == true)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }

        if (Volatile.Read(ref _persistenceAnomaly) != 0)
        {
            MarkPersistenceAnomaly(state);
        }
    }

    static void ThrowIfShuttingDown(RuntimeState state)
    {
        if (state.IsShuttingDown)
        {
            throw new PantsBusyException("The runtime is shutting down.");
        }
    }

    void ThrowIfVerificationInProgress()
    {
        if (_verificationBarrier is not null)
        {
            throw new PantsBusyException(
                "The storage layout is pinned by online verification.");
        }
    }

    async ValueTask CollectObsoleteFilesAfterSnapshotReleaseAsync(RuntimeState state)
    {
        if (_diskStore is null)
        {
            return;
        }

        DeferObsoleteFileCollectionUntilSnapshotsRelease(state);

        if (_verificationBarrier is not null)
        {
            _garbageCollectionPending = true;
            return;
        }

        var storageChanged = false;
        await _garbageCollectionWorker
            .ExecuteAsync(() => storageChanged = _diskStore.CollectObsoleteFiles(state))
            .ConfigureAwait(false);
        if (_cloudPersistence is not null &&
            (storageChanged || _cloudPersistence.HasPersistenceAnomaly))
        {
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
    }

    void DeferObsoleteFileCollectionUntilSnapshotsRelease(RuntimeState state)
    {
        if (state.ActiveSnapshotCount != 0)
        {
            Volatile.Write(ref _snapshotReleaseCollectionRequired, 1);
        }
    }

    async ValueTask ReleaseVerificationBarrierAsync(OnlineVerificationBarrier barrier)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var maintenance = await SendAsync(
                _ =>
                {
                    if (_verificationBarrier?.Token != barrier.Token)
                    {
                        return ValueTask.FromResult<OnlineVerificationMaintenance?>(null);
                    }

                    _verificationBarrier = null;
                    var completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _verificationMaintenanceCompletion = completion;
                    var pending = new OnlineVerificationMaintenance(
                        _garbageCollectionPending,
                        _recoveredMemtableFlushPending,
                        _cloudWalSealPending,
                        completion);
                    _garbageCollectionPending = false;
                    _recoveredMemtableFlushPending = false;
                    _cloudWalSealPending = false;
                    return ValueTask.FromResult<OnlineVerificationMaintenance?>(pending);
                },
                CancellationToken.None).ConfigureAwait(false);
            if (maintenance is { } pending)
            {
                _ = RunDeferredVerificationMaintenanceAsync(pending);
                barrier.Release();
            }
        }
        catch (PantsAbortedException)
        {
            // Disposal owns the remaining lease and filesystem lifetime.
        }
    }

    async Task RunDeferredVerificationMaintenanceAsync(
        OnlineVerificationMaintenance maintenance)
    {
        try
        {
            var schedule = await SendAsync(
                async state =>
                {
                    try
                    {
                        if (state.IsShuttingDown)
                        {
                            return (Compaction: false, CloudWalSeal: false);
                        }

                        if (maintenance.CollectGarbage)
                        {
                            try
                            {
                                await CollectObsoleteFilesAfterSnapshotReleaseAsync(state)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                Volatile.Write(ref _persistenceAnomaly, 1);
                                MarkPersistenceAnomaly(state);
                            }
                        }

                        if (maintenance.FlushRecoveredMemtables)
                        {
                            try
                            {
                                await FreezeRecoveredMemtablesAsync(state).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                Volatile.Write(ref _persistenceAnomaly, 1);
                                MarkPersistenceAnomaly(state);
                            }
                        }

                        try
                        {
                            await ScheduleNextImmutableFlushAttemptAsync(
                                    state,
                                    true)
                                .ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            Volatile.Write(ref _persistenceAnomaly, 1);
                            MarkPersistenceAnomaly(state);
                        }

                        return (
                            Compaction: state.ImmutableMemtableFlushes.Count == 0 &&
                                        (_backgroundCompactionPending ||
                                         _readAmplificationCompactionPending),
                            CloudWalSeal: maintenance.ScheduleCloudWalSeal);
                    }
                    finally
                    {
                        CompleteVerificationMaintenance(maintenance.Completion);
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
            if (schedule.CloudWalSeal)
            {
                ScheduleCloudWalSealDeadline();
            }

            if (schedule.Compaction)
            {
                ScheduleDeferredCompaction();
            }
        }
        catch (PantsAbortedException)
        {
        }
        catch (Exception)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    void CompleteVerificationMaintenance(TaskCompletionSource completion)
    {
        if (ReferenceEquals(_verificationMaintenanceCompletion, completion))
        {
            _verificationMaintenanceCompletion = null;
        }

        completion.TrySetResult();
    }
}
