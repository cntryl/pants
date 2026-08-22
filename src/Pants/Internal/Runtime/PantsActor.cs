using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Pants;

sealed class PantsActor : IAsyncDisposable
{
    static readonly TimeSpan InitialFlushRetryBackoff = TimeSpan.FromMilliseconds(10);
    static readonly TimeSpan MaximumFlushRetryBackoff = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ProviderStartupCleanupTimeout = TimeSpan.FromSeconds(1);

    readonly PantsRuntimeState _state;
    readonly PantsOpenOptions _options;
    readonly RuntimeTelemetry _telemetry;
    readonly RuntimeMetricsSnapshotFactory _runtimeMetricsSnapshotFactory;
    readonly PantsStorageVerificationDelegate _storageVerifier;
    readonly PantsVerificationBarrierResponseDelegate _verificationBarrierResponse;
    readonly IPantsFailpointHandler _failpoints;
    readonly Channel<IRuntimeCommand> _commands;
    readonly RuntimeWorker _walWorker;
    readonly RuntimeWorker _flushWorker;
    readonly RuntimeWorker _compactionWorker;
    readonly RuntimeWorker _manifestWorker;
    readonly RuntimeWorker _garbageCollectionWorker;
    readonly RuntimeWorker _cloudWorker;
    readonly CancellationTokenSource _loopCancellation = new();
    readonly Task _loopTask;
    readonly LocalDiskStore? _diskStore;
    readonly ICloudPersistence? _cloudPersistence;
    readonly CloudCompactionOutputPublisher? _cloudCompactionOutputPublisher;
    readonly CloudDdlCoordinator? _cloudDdlCoordinator;
    readonly CloudWalSealController? _cloudWalSealController;
    readonly CloudMemtableSegmentTracker? _cloudMemtableSegments;
    readonly CloudFlushRetryScheduler _cloudFlushRetries = new();
    readonly ConcurrentDictionary<ColumnFamilyIdentity, byte> _writeStallHints =
        new(ColumnFamilyIdentityComparer.Instance);
    readonly HybridCacheManager? _hybridCache;
    readonly CloudLeaseCoordinator? _cloudLease;
    readonly CancellationTokenSource? _cloudLeaseCancellation;
    readonly Task? _cloudLeaseHeartbeat;
    readonly bool _cloudMode;
    DatabaseSnapshot _currentSnapshot;
    int _queuedCommands;
    int _disposed;
    int _persistenceAnomaly;
    int _deferredCompactionScheduled;
    bool _shutdownRequested;
    bool _shutdownPreparationCompleted;
    OnlineVerificationBarrier? _verificationBarrier;
    TaskCompletionSource? _verificationMaintenanceCompletion;
    long _nextVerificationBarrierToken;
    bool _garbageCollectionPending;
    bool _recoveredMemtableFlushPending;
    bool _cloudWalSealPending;
    bool _backgroundCompactionEnabled;
    bool _backgroundCompactionPending;
    bool _readAmplificationCompactionPending;
    readonly bool _workersStarted;
    CancellationTokenSource? _cloudWalSealDeadlineCancellation;
    Task? _cloudWalSealDeadlineTask;
    long _walCloudDurableSequence;

    public PantsActor(
        PantsOpenOptions options,
        IPantsClock ttlClock,
        RuntimeTelemetry telemetry,
        PantsRuntimeDependencies dependencies)
    {
        _options = options;
        _backgroundCompactionEnabled = options.BackgroundCompaction;
        _telemetry = telemetry;
        _storageVerifier = dependencies.StorageVerifier;
        _verificationBarrierResponse = dependencies.VerificationBarrierResponse;
        _failpoints = dependencies.Failpoints;
        _state = new PantsRuntimeState(ttlClock);
        switch (options.Storage)
        {
            case PantsStorageConfiguration.InMemory:
                _cloudMode = false;
                break;
            case PantsStorageConfiguration.Local local:
                _diskStore = LocalDiskStore.Open(
                    local.Path,
                    _state,
                    recoveryPolicy: options.RecoveryPolicy,
                    performanceGoal: options.PerformanceGoal,
                    leaseClockSkewTolerance: options.LeaseClockSkewTolerance,
                    leaseLossCallback: options.LeaseLossCallback,
                    failpoints: dependencies.Failpoints,
                    compaction: options.Compaction,
                    targetSstSizeBytes: options.TargetSstSizeBytes,
                    blockCachePolicy: options.BlockCachePolicy,
                    blockCacheBytes: options.BlockCacheBytes,
                    leaseHeartbeatInterval: dependencies.LeaseHeartbeatInterval);
                _cloudMode = false;
                break;
            case PantsStorageConfiguration.SimulatedCloud simulated:
                var simulatedHydration = SimulatedCloudPersistence.PrepareLocalCache(
                    simulated.LocalCachePath,
                    options.RecoveryPolicy);
                if (simulatedHydration.RequiresSalvage)
                {
                    _state.MarkSalvageMode();
                }
                try
                {
                    _diskStore = LocalDiskStore.Open(
                        simulated.LocalCachePath,
                        _state,
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
                        dependencies.LeaseHeartbeatInterval,
                        simulatedHydration.RecoverySsts);
                    var simulatedPersistence = new SimulatedCloudPersistence(
                        simulated.LocalCachePath,
                        _diskStore.WriterEpoch,
                        dependencies.Failpoints);
                    _cloudPersistence = simulatedPersistence;
                    _cloudCompactionOutputPublisher = new SimulatedCloudCompactionPublisher(
                        simulated.LocalCachePath,
                        dependencies.Failpoints).PublishAsync;
                    _cloudDdlCoordinator = new CloudDdlCoordinator(
                        simulated.LocalCachePath,
                        simulatedPersistence,
                        _diskStore,
                        dependencies.Failpoints);
                    _cloudDdlCoordinator.ReconcileStartupAsync(_state, CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                    _cloudMode = true;
                }
                catch
                {
                    CleanupFailedDiskStartup(_diskStore);
                    throw;
                }
                break;
            case PantsStorageConfiguration.Cloud cloud:
                var walStore = CloudObjectStoreFactory.Create(
                    cloud.Topology.Wal,
                    options.StorageTimeout,
                    dependencies.CloudHttpClient);
                var sstStore = CloudObjectStoreFactory.Create(
                    cloud.Topology.Sst,
                    options.StorageTimeout,
                    dependencies.CloudHttpClient);
                var controlStore = CloudObjectStoreFactory.Create(
                    cloud.Topology.Control,
                    options.StorageTimeout,
                    dependencies.CloudHttpClient);
                var cloudLease = new CloudLeaseCoordinator(
                    new CloudObjectLeaseStore(controlStore, PantsCloudObjectLayout.LeaseObjectKey),
                    SystemPantsClock.Instance,
                    $"pants-{Environment.ProcessId}-{Guid.NewGuid():N}",
                    TimeSpan.FromSeconds(30),
                    options.LeaseClockSkewTolerance,
                    options.LeaseLossCallback);
                _cloudLease = cloudLease;
                try
                {
                    var cloudEpoch = cloudLease.AcquireAsync(CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                    var hydration = ProviderCloudPersistence.HydrateLocalCacheAsync(
                        cloud.LocalCachePath,
                        walStore,
                        sstStore,
                        controlStore,
                        options.RecoveryPolicy,
                        CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    if (hydration.RequiresSalvage)
                    {
                        _state.MarkSalvageMode();
                    }
                    _diskStore = LocalDiskStore.Open(
                        cloud.LocalCachePath,
                        _state,
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
                        dependencies.LeaseHeartbeatInterval,
                        hydration.RecoverySsts);
                    var providerPersistence = new ProviderCloudPersistence(
                        cloud.LocalCachePath,
                        walStore,
                        sstStore,
                        controlStore,
                        cloudLease,
                        dependencies.Failpoints);
                    _cloudPersistence = providerPersistence;
                    _cloudCompactionOutputPublisher = new ProviderCloudCompactionPublisher(
                        cloud.LocalCachePath,
                        sstStore,
                        controlStore,
                        cloudLease,
                        dependencies.Failpoints).PublishAsync;
                    providerPersistence.FenceWalCatalogAsync(CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                    _cloudDdlCoordinator = new CloudDdlCoordinator(
                        cloud.LocalCachePath,
                        providerPersistence,
                        _diskStore,
                        dependencies.Failpoints);
                    _cloudDdlCoordinator.ReconcileStartupAsync(_state, CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                    Volatile.Write(
                        ref _walCloudDurableSequence,
                        checked((long)hydration.CloudDurableSequence));
                    _cloudLeaseCancellation = new CancellationTokenSource();
                    _cloudLeaseHeartbeat = RunCloudLeaseHeartbeatAsync(
                        cloudLease,
                        dependencies.LeaseHeartbeatInterval,
                        _cloudLeaseCancellation.Token);
                    _cloudMode = true;
                }
                catch
                {
                    CleanupFailedProviderStartup(
                        cloudLease,
                        _diskStore,
                        _cloudLeaseCancellation,
                        _cloudLeaseHeartbeat);
                    throw;
                }
                break;
            default:
                throw PantsException.Create(PantsErrorCode.NotSupported, "Unknown storage backend.");
        }

        try
        {
            _hybridCache = options.Storage switch
            {
                PantsStorageConfiguration.SimulatedCloud simulated => new HybridCacheManager(
                    simulated.LocalStorageBudgetBytes ??
                    HybridStorageBudgetPolicy.DefaultMaximumLocalBytes),
                PantsStorageConfiguration.Cloud => new HybridCacheManager(
                    HybridStorageBudgetPolicy.DefaultMaximumLocalBytes),
                _ => null
            };

            if (_cloudMode && _diskStore is not null && _cloudPersistence is not null)
            {
                _ = _diskStore.SealActiveWal();
                DrainCloudWalBacklogAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                _cloudPersistence.CollectObsoleteSstsAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                ApplyPendingPersistenceAnomaly(_state);
            }

            if (_cloudMode && _diskStore is not null)
            {
                _cloudWalSealController = new CloudWalSealController(
                    options.CloudWritePolicy,
                    TimeProvider.System);
                _cloudMemtableSegments = new CloudMemtableSegmentTracker();
                _cloudMemtableSegments.Reinitialize(
                    _state.ActiveMemtableBytes
                        .Where(static entry => entry.Value > 0)
                        .Select(static entry => entry.Key),
                    _diskStore.CurrentWalSegmentId);
            }

            _walWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _flushWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _compactionWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _manifestWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _garbageCollectionWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _cloudWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
            _runtimeMetricsSnapshotFactory = new RuntimeMetricsSnapshotFactory(
                options,
                telemetry,
                _diskStore,
                _compactionWorker,
                _cloudFlushRetries,
                _cloudWalSealController,
                _cloudMemtableSegments,
                _hybridCache);
            _workersStarted = true;
            _commands = Channel.CreateBounded<IRuntimeCommand>(new BoundedChannelOptions(
                options.CoordinatorQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _currentSnapshot = _state.CreateSnapshot();
            _loopTask = Task.Run(RunLoopAsync);
            if (UsesBackgroundImmutableFlushes)
            {
                _ = ScheduleRecoveredMemtableFlushesAsync();
            }
        }
        catch
        {
            if (_cloudLease is not null)
            {
                CleanupFailedProviderStartup(
                    _cloudLease,
                    _diskStore,
                    _cloudLeaseCancellation,
                    _cloudLeaseHeartbeat);
            }
            else
            {
                CleanupFailedDiskStartup(_diskStore);
            }

            throw;
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

    static void CleanupFailedProviderStartup(
        CloudLeaseCoordinator cloudLease,
        LocalDiskStore? diskStore,
        CancellationTokenSource? heartbeatCancellation,
        Task? heartbeat)
    {
        try
        {
            heartbeatCancellation?.Cancel();
            heartbeat?.WaitAsync(ProviderStartupCleanupTimeout)
                .GetAwaiter().GetResult();
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
            cloudLease.ReleaseAsync(cancellation.Token)
                .AsTask().GetAwaiter().GetResult();
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

    public bool IsPrimaryLeaseHealthy =>
        (_diskStore?.IsLeaseHealthy ?? true) && (_cloudLease?.IsHealthy ?? true);

    public bool IsSupported(PantsDurability durability) => _cloudMode
        ? durability is PantsDurability.BestEffort or PantsDurability.CloudAsync or PantsDurability.CloudStrict
        : durability is PantsDurability.Sync or PantsDurability.Buffered or PantsDurability.BestEffort;

    public ValueTask<ColumnFamilyIdentity> CreateColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken) =>
        SendAsync(
            async state =>
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
                    await _cloudWorker.ExecuteAsync(workerCancellationToken =>
                            _cloudDdlCoordinator.ExecuteAsync(
                                state,
                                edit,
                                workerCancellationToken))
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
                    state.FamilyData[created] = new SortedDictionary<byte[], CellState>(
                        ByteArrayComparer.Instance);
                    state.RangeTombstones[created] = [];
                    state.ActiveMemtableBytes[created] = 0;
                }

                if (_cloudPersistence is not null)
                {
                    try
                    {
                        await _cloudWorker.ExecuteAsync(
                                _cloudPersistence.MirrorMetadataAndSstsAsync)
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
                        retryFailures: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                _failpoints.Hit(PantsFailpoint.BeforeDropAdmission);
            }

            var dropped = await SendAsync(
                async state =>
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
                        await _cloudWorker.ExecuteAsync(workerCancellationToken =>
                                _cloudDdlCoordinator.ReconcilePendingAsync(
                                    state,
                                    workerCancellationToken))
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
                        await _cloudWorker.ExecuteAsync(workerCancellationToken =>
                                _cloudDdlCoordinator.ExecuteAsync(
                                    state,
                                    edit,
                                    workerCancellationToken))
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
                            await _cloudWorker.ExecuteAsync(
                                    _cloudPersistence.MirrorMetadataAndSstsAsync)
                                .ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            MarkPersistenceAnomaly(state);
                        }
                    }

                    if (_diskStore is not null)
                    {
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
                                await _cloudWorker.ExecuteAsync(
                                        _cloudPersistence.MirrorMetadataAndSstsAsync)
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
        DatabaseSnapshot snapshot,
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
            cancellationToken);

    public async ValueTask ReleaseScanSnapshotAsync(
        long snapshotId,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
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
        PantsDatabaseInstance database,
        PantsColumnFamilyHandle columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                ThrowIfShuttingDown(state);
                var identity = columnFamily.Identity;
                ValidateActiveFamily(state, identity);
                var transactionId = checked(++state.TransactionCounter);
                var snapshot = state.CreateSnapshot();
                var transaction = new PantsTransactionInstance(
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
                PantsDiagnostics.TransactionsStarted.Add(1);
                return ValueTask.FromResult<IPantsTransaction>(transaction);
            },
            cancellationToken);

    public async ValueTask CommitAsync(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Mode != PantsTransactionMode.ReadOnly &&
            !IsSupported(writeOptions.Durability))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Durability '{writeOptions.Durability}' is not valid for this storage backend.");
        }

        var families = GetCommitFamilies(payload);
        if (families.Any(_writeStallHints.ContainsKey))
        {
            await EnsureWriteAdmissionAsync(families, cancellationToken).ConfigureAwait(false);
        }

        await SendCommitAsync(
            writeOptions,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RollbackAsync(long transactionId, CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                if (state.ActiveTransactions.Remove(transactionId))
                {
                    _telemetry.RecordSnapshotUnregister();
                    PantsDiagnostics.TransactionsRolledBack.Add(1);
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
            _failpoints.Hit(PantsFailpoint.BeforeFlushCompactionAdmission);
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

        await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                EnsureCloudWriteAuthorityValid();
                ValidateActiveFamily(state, identity);
                if (_diskStore is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    await _flushWorker
                        .ExecuteAsync(() => _diskStore.Flush(state, identity))
                        .ConfigureAwait(false);
                    _telemetry.RecordFlush(Stopwatch.GetElapsedTime(started));
                }

                try
                {
                    await MirrorCloudStorageAsync().ConfigureAwait(false);
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
        while (true)
        {
            if (UsesBackgroundImmutableFlushes)
            {
                await FlushActiveAndImmutableMemtablesAsync(cancellationToken)
                    .ConfigureAwait(false);
                _failpoints.Hit(PantsFailpoint.BeforeCompactionAdmission);
            }

            var compacted = await SendAsync(
                async state =>
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
                        await EnsureHybridSstsLocalAsync(
                                _diskStore.GetManifestSstNames(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        var result = default(CompactionResult);
                        await _compactionWorker
                            .ExecuteAsync(async workerCancellationToken =>
                                result = await _diskStore.CompactAsync(
                                        state,
                                        force: true,
                                        _cloudCompactionOutputPublisher,
                                        workerCancellationToken)
                                    .ConfigureAwait(false))
                            .ConfigureAwait(false);
                        if (result.PersistenceAnomaly)
                        {
                            Volatile.Write(ref _persistenceAnomaly, 1);
                            MarkPersistenceAnomaly(state);
                        }

                        if (result.BytesRewritten > 0)
                        {
                            _telemetry.RecordCompaction(result.BytesRewritten);
                        }
                    }

                    await MirrorCloudStorageAsync().ConfigureAwait(false);
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
        PantsRuntimeState state,
        CommitPayload payload)
    {
        if (_diskStore is null)
        {
            return;
        }

        var familiesOverLimit = payload.OrderedOperations
            .GroupBy(static operation => operation.Family, ColumnFamilyIdentityComparer.Instance)
            .Where(group =>
                state.ActiveMemtableBytes.GetValueOrDefault(group.Key) > 0 &&
                state.ActiveMemtableBytes.GetValueOrDefault(group.Key) +
                group.Sum(EstimateOperationBytes) > _options.MemtableSizeLimitBytes)
            .Select(static group => group.Key)
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
                        rejectWhenQueueFull: false)
                    .ConfigureAwait(false);
            }

            return;
        }

        await _flushWorker.ExecuteAsync(() => _diskStore.Flush(state)).ConfigureAwait(false);
        await MirrorCloudStorageAsync().ConfigureAwait(false);
        state.UnflushedFamilies.Clear();
        ClearMemtableAccounting(state);
        await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
    }

    public ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                ApplyPendingPersistenceAnomaly(state);
                return ValueTask.FromResult(_runtimeMetricsSnapshotFactory.Create(
                    state,
                    Volatile.Read(ref _walCloudDurableSequence)));
            },
            cancellationToken);

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

    public async ValueTask RecordPointReadAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        _ = await SendAsync(
            async state =>
            {
                bool exceedsBudget;
                if (_diskStore is null)
                {
                    exceedsBudget = _telemetry.RecordSstRead(default);
                }
                else
                {
                    await EnsureHybridSstsLocalAsync(
                            _diskStore.GetPointReadSstNames(columnFamily, key.Span),
                            cancellationToken)
                        .ConfigureAwait(false);
                    exceedsBudget = _diskStore.RecordPointRead(
                        _telemetry,
                        columnFamily,
                        key.Span);
                }

                if (exceedsBudget && _backgroundCompactionEnabled && _diskStore is not null)
                {
                    await RunReadAmplificationCompactionAsync(state).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IScanReadValidator?> CreateScanReadValidatorAsync(
        ColumnFamilyIdentity columnFamily,
        PantsScanBounds bounds,
        CancellationToken cancellationToken)
        => SendAsync(
            async _ =>
            {
                if (_diskStore is not null)
                {
                    await EnsureHybridSstsLocalAsync(
                            _diskStore.GetScanSstNames(columnFamily, bounds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var validator = _diskStore?.CreateScanReadValidator(
                    _telemetry,
                    columnFamily,
                    bounds);
                return validator;
            },
            cancellationToken);

    public ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(
            state => ValueTask.FromResult(new PantsRecoveryMetrics(
                _diskStore?.WalRecoveryRecordsReplayed ?? 0,
                _diskStore?.WalRecoveryBytesReplayed ?? 0,
                state.IntentLogReplayRuns,
                state.IntentLogEntriesReplayed)),
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
                            Barrier: (OnlineVerificationBarrier?)null,
                            RetryAfter: (Task?)_verificationBarrier.Released,
                            LayoutBusy: false);
                    }

                    if (_verificationMaintenanceCompletion is { } maintenanceCompletion)
                    {
                        return (
                            Barrier: (OnlineVerificationBarrier?)null,
                            RetryAfter: (Task?)maintenanceCompletion.Task,
                            LayoutBusy: false);
                    }

                    if (HasLayoutMutationInFlight(state))
                    {
                        return (
                            Barrier: (OnlineVerificationBarrier?)null,
                            RetryAfter: (Task?)null,
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
                        _failpoints.Hit(PantsFailpoint.BeforeVerificationBarrierResponse);
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

    bool HasLayoutMutationInFlight(PantsRuntimeState state) =>
        state.ImmutableMemtableFlushes.Count != 0 ||
        _walWorker.Outstanding != 0 ||
        _flushWorker.Outstanding != 0 ||
        _compactionWorker.Outstanding != 0 ||
        _manifestWorker.Outstanding != 0 ||
        _garbageCollectionWorker.Outstanding != 0 ||
        _cloudWorker.Outstanding != 0;

    PantsEngineHealth CaptureRuntimeHealth(PantsRuntimeState state)
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
                    state.ActiveScanSnapshots.Clear();
                    foreach (var flush in state.ImmutableMemtableFlushes.Values)
                    {
                        flush.FailWaiterForShutdown();
                    }

                    state.SignalWritePressureChanged();
                }

                return ValueTask.FromResult(
                    _verificationBarrier?.Released ?? Task.CompletedTask);
            },
            CancellationToken.None).AsTask();
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
                    await _walWorker.ExecuteAsync(() =>
                        {
                            _failpoints.Hit(PantsFailpoint.BeforeShutdownWalDurabilityBoundary);
                            _diskStore.FlushDurabilityBoundary();
                        })
                        .ConfigureAwait(false);
                    if (_cloudPersistence is not null && (_cloudLease?.IsHealthy ?? true))
                    {
                        var segment = await SealWalForCloudAsync(_diskStore)
                            .ConfigureAwait(false);
                        if (segment is not null)
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
            CancellationToken.None).AsTask();
        try
        {
            await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveShutdownPreparationAsync(preparation);
            throw;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelCloudWalSealDeadline();
        var cloudWalSealDeadlineTask = Volatile.Read(ref _cloudWalSealDeadlineTask);
        if (cloudWalSealDeadlineTask is not null)
        {
            try
            {
                await cloudWalSealDeadlineTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _cloudFlushRetries.DisposeAsync().ConfigureAwait(false);
        _commands.Writer.TryComplete();
        await _loopTask.ConfigureAwait(false);
        await _cloudWorker.DisposeAsync().ConfigureAwait(false);
        await _walWorker.DisposeAsync().ConfigureAwait(false);
        await _flushWorker.DisposeAsync().ConfigureAwait(false);
        await _compactionWorker.DisposeAsync().ConfigureAwait(false);
        await _manifestWorker.DisposeAsync().ConfigureAwait(false);
        await _garbageCollectionWorker.DisposeAsync().ConfigureAwait(false);
        if (_cloudLeaseCancellation is not null)
        {
            await _cloudLeaseCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_cloudLeaseHeartbeat is not null)
        {
            await _cloudLeaseHeartbeat.ConfigureAwait(false);
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

        _cloudLeaseCancellation?.Dispose();
        _cloudLease?.Dispose();
        _diskStore?.Dispose();
        _loopCancellation.Dispose();
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

    async ValueTask<T> SendAsync<T>(
        Func<PantsRuntimeState, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is disposed.");
        }

        var command = new RuntimeCommand<T>(operation);
        Interlocked.Increment(ref _queuedCommands);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            return await command.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PantsDiagnostics.CommandsRejected.Add(1);
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
            Interlocked.Decrement(ref _queuedCommands);
            PantsDiagnostics.CommandLatencyMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    async ValueTask SendCommitAsync(
        PantsWriteOptions writeOptions,
        CommitPayload payload,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is disposed.");
        }

        var command = new CommitRuntimeCommand(
            writeOptions,
            payload,
            state => ExecuteCommitAsync(state, writeOptions, payload));
        Interlocked.Increment(ref _queuedCommands);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            var writeStalled = await command.Task.ConfigureAwait(false);
            if (writeStalled)
            {
                foreach (var family in GetCommitFamilies(payload))
                {
                    _writeStallHints.TryAdd(family, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            PantsDiagnostics.CommandsRejected.Add(1);
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
            Interlocked.Decrement(ref _queuedCommands);
            PantsDiagnostics.CommandLatencyMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    async Task RunLoopAsync()
    {
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

                if (CanCoalesceSyncCommits(commits))
                {
                    await ExecuteCoalescedSyncCommitsAsync(_state, commits).ConfigureAwait(false);
                }
                else
                {
                    foreach (var commit in commits)
                    {
                        await commit.ExecuteAsync(_state).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_loopCancellation.IsCancellationRequested)
        {
        }
    }

    bool CanCoalesceSyncCommits(List<CommitRuntimeCommand> commits) =>
        commits.Count > 1 &&
        _diskStore is not null &&
        _cloudPersistence is null &&
        _options.FlushAfterWalRecords == 0 &&
        commits.All(static command =>
            command.WriteOptions.Durability == PantsDurability.Sync &&
            command.Payload.OrderedOperations.Count != 0);

    async ValueTask ExecuteCoalescedSyncCommitsAsync(
        PantsRuntimeState state,
        List<CommitRuntimeCommand> commits)
    {
        var diskStore = _diskStore ??
            throw new PantsInternalException("A coalesced commit requires persistent storage.");
        var accepted = new List<CommitRuntimeCommand>(commits.Count);
        for (var index = 0; index < commits.Count; index++)
        {
            var command = commits[index];
            try
            {
                await PrepareCommitAsync(state, command.Payload).ConfigureAwait(false);
                var started = Stopwatch.GetTimestamp();
                await _walWorker.ExecuteAsync(() => diskStore.AppendCommit(
                        command.Payload,
                        state,
                        PantsDurability.Buffered))
                    .ConfigureAwait(false);
                _telemetry.RecordWalAppend(
                    Stopwatch.GetElapsedTime(started),
                    PantsDurability.Buffered,
                    state.Sequence);
                ApplyCommittedOperations(state, command.Payload);
                accepted.Add(command);
            }
            catch (Exception exception)
            {
                command.Fail(state, exception);
                if (exception is PantsWriteConflictException)
                {
                    continue;
                }

                for (var remaining = index + 1; remaining < commits.Count; remaining++)
                {
                    commits[remaining].Fail(
                        state,
                        new PantsAbortedException(
                            "The coalesced commit group stopped after a persistence failure.",
                            exception));
                }

                break;
            }
        }

        if (accepted.Count == 0)
        {
            return;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            await _walWorker.ExecuteAsync(diskStore.FlushDurabilityBoundary).ConfigureAwait(false);
            _telemetry.RecordCoalescedWalFsync(
                Stopwatch.GetElapsedTime(started),
                accepted.Count,
                state.Sequence);
            foreach (var command in accepted)
            {
                await FlushAtConfiguredThresholdAsync(state, command.Payload).ConfigureAwait(false);
                PantsDiagnostics.TransactionsCommitted.Add(1);
            }

            await RotateLocalWalAtConfiguredThresholdAsync(state, diskStore).ConfigureAwait(false);
            PublishSnapshot(state);
            foreach (var command in accepted)
            {
                command.Complete(IsCommitWriteStalled(state, command.Payload));
            }
        }
        catch (Exception exception)
        {
            foreach (var command in accepted)
            {
                command.Fail(state, exception);
            }
        }
    }

    async ValueTask<bool> ExecuteCommitAsync(
        PantsRuntimeState state,
        PantsWriteOptions writeOptions,
        CommitPayload payload)
    {
        EnsureCloudWriteAuthorityValid();
        await PrepareCommitAsync(state, payload).ConfigureAwait(false);
        if (payload.OrderedOperations.Count != 0)
        {
            ulong? writtenWalSegmentId = null;
            if (_diskStore is null)
            {
                state.Sequence++;
            }
            else
            {
                writtenWalSegmentId = _diskStore.CurrentWalSegmentId;
                await PersistCommitAsync(state, payload, writeOptions.Durability)
                    .ConfigureAwait(false);
            }

            ApplyCommittedOperations(state, payload);
            if (writtenWalSegmentId.HasValue)
            {
                TrackCloudMemtableWrites(payload, writtenWalSegmentId.Value);
            }

            await FlushAtWalRecordThresholdAsync(state).ConfigureAwait(false);
            await FlushAtConfiguredThresholdAsync(state, payload).ConfigureAwait(false);
        }
        else if (payload.Mode == PantsTransactionMode.ReadWrite &&
                 writeOptions.Durability == PantsDurability.Sync &&
                 _diskStore is not null)
        {
            var started = Stopwatch.GetTimestamp();
            await _walWorker.ExecuteAsync(_diskStore.FlushDurabilityBoundary).ConfigureAwait(false);
            _telemetry.RecordWalFsyncBoundary(Stopwatch.GetElapsedTime(started), state.Sequence);
        }

        PublishSnapshot(state);
        PantsDiagnostics.TransactionsCommitted.Add(1);
        return IsCommitWriteStalled(state, payload);
    }

    async ValueTask PrepareCommitAsync(PantsRuntimeState state, CommitPayload payload)
    {
        ThrowIfShuttingDown(state);
        ThrowIfVerificationInProgress();
        if (!state.ActiveTransactions.Remove(payload.TransactionId))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Transaction {payload.TransactionId} is not active.");
        }

        _telemetry.RecordSnapshotUnregister();
        if (_diskStore is not null)
        {
            if (payload.OrderedOperations.Count != 0)
            {
                _hybridCache?.EnsureWriteAdmitted(_diskStore, state);
            }

            var storageChanged = false;
            await _garbageCollectionWorker
                .ExecuteAsync(() =>
                    storageChanged = _diskStore.CollectObsoleteFiles(state))
                .ConfigureAwait(false);
            if (_cloudPersistence is not null &&
                (storageChanged || _cloudPersistence.HasPersistenceAnomaly))
            {
                await MirrorCloudStorageAsync().ConfigureAwait(false);
            }
            else if (_cloudPersistence is not null && payload.OrderedOperations.Count != 0)
            {
                await _cloudWorker.ExecuteAsync(_cloudPersistence.ValidateWriteAuthorityAsync)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            CommitValidator.Validate(state, payload);
        }
        catch (PantsException exception) when (exception.Code == PantsErrorCode.WriteConflict)
        {
            _telemetry.RecordWriteConflict(CommitValidator.HasRangeConflict(state, payload));
            PantsDiagnostics.TransactionsConflicted.Add(1);
            throw;
        }

        if (payload.OrderedOperations.Count != 0)
        {
            await RelieveWritePressureAsync(state, payload).ConfigureAwait(false);
        }
    }

    static void ApplyCommittedOperations(PantsRuntimeState state, CommitPayload payload)
    {
        ApplyOperations(state, payload, state.Sequence);
        RecordMemtableBytes(state, payload);
        foreach (var family in payload.Writes.Keys.Concat(payload.DeleteRanges.Keys))
        {
            state.UnflushedFamilies.Add(family);
        }
    }

    async ValueTask PersistCommitAsync(
        PantsRuntimeState state,
        CommitPayload payload,
        PantsDurability durability)
    {
        var diskStore = _diskStore ??
            throw new PantsInternalException("Persistent commit has no disk store.");
        var started = Stopwatch.GetTimestamp();
        await _walWorker.ExecuteAsync(() => diskStore.AppendCommit(
                payload,
                state,
                durability is PantsDurability.CloudAsync or PantsDurability.CloudStrict
                    ? PantsDurability.Buffered
                    : durability))
            .ConfigureAwait(false);
        if (durability != PantsDurability.BestEffort)
        {
            _telemetry.RecordWalAppend(
                Stopwatch.GetElapsedTime(started),
                durability,
                state.Sequence);
        }
        if (!UsesBackgroundImmutableFlushes &&
            _options.FlushAfterWalRecords > 0 &&
            diskStore.WalRecords >= _options.FlushAfterWalRecords)
        {
            await _flushWorker.ExecuteAsync(() => diskStore.Flush(state)).ConfigureAwait(false);
            await MirrorCloudStorageAsync().ConfigureAwait(false);
            state.UnflushedFamilies.Clear();
            await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
        }

        await RotateLocalWalAtConfiguredThresholdAsync(state, diskStore).ConfigureAwait(false);

        if (_cloudPersistence is not null && durability == PantsDurability.CloudAsync)
        {
            var controller = _cloudWalSealController ??
                throw new PantsInternalException("CloudAsync has no WAL seal controller.");
            controller.RecordWrite();
            if (controller.ShouldSeal(diskStore.ActiveWalBytes))
            {
                await SealCloudAsyncWalAsync(diskStore).ConfigureAwait(false);
            }
            else
            {
                ScheduleCloudWalSealDeadline();
                await EnqueueCloudWalBacklogDrainAsync().ConfigureAwait(false);
            }
        }
        else if (_cloudPersistence is not null && durability == PantsDurability.CloudStrict)
        {
            var segment = await SealWalForCloudAsync(diskStore)
                .ConfigureAwait(false);
            if (segment is not null)
            {
                _cloudWalSealController?.RecordSeal();
                CancelCloudWalSealDeadline();
            }

            try
            {
                await _cloudWorker.ExecuteAsync(DrainCloudWalBacklogWithFailureTrackingAsync)
                    .ConfigureAwait(false);
            }
            catch (PantsIOException exception)
            {
                throw new PantsInternalException(
                    "Cloud-strict WAL publication failed before acknowledgement.",
                    exception);
            }
        }

    }

    async ValueTask SealCloudAsyncWalAsync(LocalDiskStore diskStore)
    {
        EnsureCloudWriteAuthorityValid();
        var segment = await SealWalForCloudAsync(diskStore).ConfigureAwait(false);
        if (segment is null)
        {
            return;
        }

        _cloudWalSealController?.RecordSeal();
        CancelCloudWalSealDeadline();
        await EnqueueCloudWalBacklogDrainAsync().ConfigureAwait(false);
    }

    async ValueTask<SealedWalSegment?> SealWalForCloudAsync(LocalDiskStore diskStore)
    {
        SealedWalSegment? segment = null;
        var started = Stopwatch.GetTimestamp();
        await _walWorker.ExecuteAsync(() => segment = diskStore.SealActiveWal())
            .ConfigureAwait(false);
        if (segment is not null)
        {
            _telemetry.RecordWalDurabilityBoundary(checked((long)segment.MaximumSequence));
            _telemetry.RecordCloudAsyncWalSegmentSealed(
                segment.SegmentId,
                segment.Bytes.LongLength,
                Stopwatch.GetElapsedTime(started));
        }

        return segment;
    }

    ValueTask EnqueueCloudWalBacklogDrainAsync() =>
        _cloudWorker.EnqueueAsync(DrainCloudWalBacklogWithFailureTrackingAsync);

    void ScheduleCloudWalSealDeadline()
    {
        var delay = _cloudWalSealController?.RemainingDelay;
        if (!delay.HasValue)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loopCancellation.Token);
        var previous = Interlocked.Exchange(
            ref _cloudWalSealDeadlineCancellation,
            cancellation);
        previous?.Cancel();
        _cloudWalSealDeadlineTask = RunCloudWalSealDeadlineAsync(
            delay.Value,
            cancellation);
    }

    async Task RunCloudWalSealDeadlineAsync(
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            _ = await SendAsync(
                async state =>
                {
                    if (_verificationBarrier is not null)
                    {
                        _cloudWalSealPending = true;
                        return true;
                    }

                    if (_diskStore is not null &&
                        _cloudWalSealController?.PendingWrites > 0)
                    {
                        await SealCloudAsyncWalAsync(_diskStore).ConfigureAwait(false);
                        await FlushCloudSegmentGapAsync(state).ConfigureAwait(false);
                        PublishSnapshot(state);
                    }

                    return true;
                },
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (PantsException)
        {
            // The locally durable WAL remains available for the next commit,
            // shutdown, or recovery retry.
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _cloudWalSealDeadlineCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    void CancelCloudWalSealDeadline() =>
        Interlocked.Exchange(ref _cloudWalSealDeadlineCancellation, null)?.Cancel();

    async ValueTask PublishCloudWalAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken)
    {
        EnsureCloudWriteAuthorityValid();
        var persistence = _cloudPersistence ??
            throw new PantsInternalException("Cloud WAL publication has no persistence backend.");
        var started = Stopwatch.GetTimestamp();
        _telemetry.RecordCloudUploadPending();
        _telemetry.RecordCloudAsyncWalUploadStarted();
        try
        {
            _failpoints.Hit(PantsFailpoint.BeforeCloudWalUpload);
            await persistence.PublishWalAsync(segment, cancellationToken).ConfigureAwait(false);
            _failpoints.Hit(PantsFailpoint.AfterCloudWalUpload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _telemetry.RecordCloudAsyncWalUploadFailed();
            throw;
        }
        finally
        {
            _telemetry.RecordCloudUploadCompleted();
        }

        _telemetry.RecordCloudAsyncWalUploadCompleted(Stopwatch.GetElapsedTime(started));
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
                await _walWorker.ExecuteAsync(() =>
                        _diskStore.DeleteCloudDurableWalSegment(segment),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _diskStore.DeleteCloudDurableWalSegment(segment);
            }
        }

        _telemetry.RecordCloudAsyncWalAcknowledged(segment.SegmentId);
    }

    async ValueTask DrainCloudWalBacklogAsync(CancellationToken cancellationToken)
    {
        EnsureCloudWriteAuthorityValid();
        if (_diskStore is null)
        {
            return;
        }

        IReadOnlyList<SealedWalSegment> segments = [];
        if (_workersStarted)
        {
            await _walWorker.ExecuteAsync(() =>
                    segments = _diskStore.GetSealedWalSegmentsForCloudPublication(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            segments = _diskStore.GetSealedWalSegmentsForCloudPublication();
        }

        foreach (var segment in segments)
        {
            EnsureCloudWriteAuthorityValid();
            await PublishCloudWalAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask DrainCloudWalBacklogWithFailureTrackingAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await DrainCloudWalBacklogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PantsException) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            throw;
        }
    }

    async ValueTask TryDrainCloudWalBacklogOnShutdownAsync(PantsRuntimeState state)
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

    async ValueTask TryMirrorCloudStorageOnShutdownAsync(PantsRuntimeState state)
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
        PantsRuntimeState state,
        LocalDiskStore diskStore)
    {
        if (_options.Storage is not PantsStorageConfiguration.Local ||
            UsesBackgroundImmutableFlushes && state.ImmutableMemtableFlushes.Count != 0 ||
            diskStore.ActiveWalBytes < _options.WalBufferSizeBytes)
        {
            return;
        }

        SealedWalSegment? segment = null;
        await _walWorker.ExecuteAsync(() => segment = diskStore.SealActiveWal())
            .ConfigureAwait(false);
        if (segment is not null)
        {
            _telemetry.RecordWalDurabilityBoundary(checked((long)segment.MaximumSequence));
        }
    }

    static void ApplyOperations(PantsRuntimeState state, CommitPayload payload, long sequence)
    {
        foreach (var operation in payload.OrderedOperations)
        {
            var family = GetFamily(state, operation.Family);
            switch (operation.Kind)
            {
                case CommitOperationKind.Put:
                    family[operation.Key.ToArray()] = new CellState(
                        operation.Value?.ToArray(),
                        sequence,
                        operation.ExpiryUtc);
                    break;
                case CommitOperationKind.Delete:
                    family[operation.Key.ToArray()] = new CellState(null, sequence, null);
                    break;
                case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                    state.RangeTombstones[operation.Family].Add(new CommittedRangeTombstone(
                        operation.Key.ToArray(),
                        operation.EndExclusive.ToArray(),
                        sequence));
                    foreach (var key in family.Keys
                                 .Where(key => IsInRange(key, operation.Key, operation.EndExclusive))
                                 .ToArray())
                    {
                        family[key] = new CellState(null, sequence, null);
                    }

                    break;
                default:
                    throw PantsException.Create(
                        PantsErrorCode.Internal,
                        $"Unsupported transaction operation '{operation.Kind}'.");
            }
        }
    }

    static void ValidateActiveFamily(PantsRuntimeState state, ColumnFamilyIdentity identity)
    {
        if (!IsActiveFamily(state, identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

    static bool IsActiveFamily(PantsRuntimeState state, ColumnFamilyIdentity identity) =>
        state.ActiveFamilyVersions.TryGetValue(identity.Name, out var activeGeneration) &&
        activeGeneration == identity.Generation &&
        state.FamilyData.ContainsKey(identity);

    static SortedDictionary<byte[], CellState> GetFamily(
        PantsRuntimeState state,
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
        PantsRuntimeState state,
        CommitPayload payload)
    {
        if (_diskStore is null)
        {
            return;
        }

        if (payload.OrderedOperations.Any(operation =>
                state.ActiveMemtableBytes.GetValueOrDefault(operation.Family) >=
                _options.MemtableFlushThresholdBytes))
        {
            if (UsesBackgroundImmutableFlushes)
            {
                foreach (var family in payload.OrderedOperations
                             .Select(static operation => operation.Family)
                             .Distinct(ColumnFamilyIdentityComparer.Instance))
                {
                    if (state.ActiveMemtableBytes.GetValueOrDefault(family) >=
                        _options.MemtableFlushThresholdBytes)
                    {
                        await FreezeAndScheduleFlushAsync(
                                state,
                                family,
                                rejectWhenQueueFull: false)
                            .ConfigureAwait(false);
                    }
                }

                return;
            }

            var started = Stopwatch.GetTimestamp();
            await _flushWorker.ExecuteAsync(() => _diskStore.Flush(state)).ConfigureAwait(false);
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

    async ValueTask FlushAtWalRecordThresholdAsync(PantsRuntimeState state)
    {
        if (!UsesBackgroundImmutableFlushes ||
            _options.FlushAfterWalRecords <= 0 ||
            _diskStore!.WalRecords < _options.FlushAfterWalRecords)
        {
            return;
        }

        foreach (var family in state.ActiveMemtableBytes
                     .Where(static pair => pair.Value > 0)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _ = await FreezeAndScheduleFlushAsync(
                    state,
                    family,
                    rejectWhenQueueFull: false)
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

    async ValueTask FreezeRecoveredMemtablesAsync(PantsRuntimeState state)
    {
        foreach (var family in state.ActiveMemtableBytes
                     .Where(pair => pair.Value >= _options.MemtableFlushThresholdBytes)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _ = await FreezeAndScheduleFlushAsync(
                    state,
                    family,
                    rejectWhenQueueFull: false)
                .ConfigureAwait(false);
        }
    }

    bool UsesBackgroundImmutableFlushes =>
        _diskStore is not null && _options.Storage is PantsStorageConfiguration.Local;

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
                await ScheduleNextImmutableFlushAttemptAsync(state, retryFailure: true)
                    .ConfigureAwait(false);
                return state.ImmutableMemtableFlushes.Values
                    .Where(flush => flush.Frozen.ColumnFamily == identity)
                    .Select(static flush => flush.AttemptTask)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await AwaitFlushAttemptsAsync(attempts, cancellationToken).ConfigureAwait(false);
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
                    await ScheduleNextImmutableFlushAttemptAsync(state, retryFailure: true)
                        .ConfigureAwait(false);
                }

                return flushes.Select(static flush => flush.AttemptTask).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await AwaitFlushAttemptsAsync(attempts, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<ImmutableMemtableFlush?> FreezeAndScheduleFlushAsync(
        PantsRuntimeState state,
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
        await ScheduleNextImmutableFlushAttemptAsync(state, retryFailure: false)
            .ConfigureAwait(false);
        PublishSnapshot(state);
        return flush;
    }

    async ValueTask ScheduleNextImmutableFlushAttemptAsync(
        PantsRuntimeState state,
        bool retryFailure)
    {
        if (state.IsShuttingDown)
        {
            return;
        }

        var next = state.ImmutableMemtableFlushes.Values
            .OrderBy(static flush => flush.Frozen.FrontierSequence)
            .ThenBy(static flush => flush.Frozen.Id)
            .ThenBy(static flush => flush.Frozen.ColumnFamilyId)
            .FirstOrDefault();
        if (next is null || next.IsRunning || next.HasFailed && !retryFailure)
        {
            return;
        }

        await ScheduleImmutableFlushAttemptAsync(next).ConfigureAwait(false);
    }

    async ValueTask ScheduleImmutableFlushAttemptAsync(ImmutableMemtableFlush flush)
    {
        var diskStore = _diskStore ??
            throw new PantsInternalException("An immutable flush requires local storage.");
        if (flush.Attempts > 0)
        {
            _telemetry.RecordFlushRetry();
        }

        flush.BeginAttempt();
        var workerTask = await _flushWorker.ScheduleAsync(_ =>
            {
                if (flush.PublicationPlan is null)
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        flush.PublicationPlan = diskStore.BuildFrozenFlushPlan(flush.Frozen);
                    }
                    finally
                    {
                        _telemetry.RecordFlushBuild(Stopwatch.GetElapsedTime(started));
                    }
                }

                var publicationStarted = Stopwatch.GetTimestamp();
                try
                {
                    var result = diskStore.PublishFrozenFlushPlan(
                        flush.Frozen,
                        flush.PublicationPlan);
                    flush.PersistenceAnomaly |= result.PersistenceAnomaly;
                }
                finally
                {
                    _telemetry.RecordFlushPublication(
                        Stopwatch.GetElapsedTime(publicationStarted));
                }
                return ValueTask.CompletedTask;
            })
            .ConfigureAwait(false);
        flush.AttachRunningTask(workerTask);
        _ = ObserveImmutableFlushAttemptAsync(flush, workerTask);
    }

    async Task ObserveImmutableFlushAttemptAsync(
        ImmutableMemtableFlush expected,
        Task workerTask)
    {
        var failure = (Exception?)null;
        try
        {
            await workerTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = RuntimeExceptionMapper.ToPublicException(exception);
        }

        try
        {
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

                    if (failure is not null)
                    {
                        if (failure is PantsNoSpaceException)
                        {
                            state.NoSpaceEvents = checked(state.NoSpaceEvents + 1);
                            _telemetry.RecordWriteStallNoSpace();
                        }

                        _telemetry.RecordFlushFailure();
                        current.CompleteAttempt(failure);
                        if (!state.IsShuttingDown)
                        {
                            _ = RetryImmutableFlushAfterDelayAsync(
                                current,
                                current.Attempts,
                                GetFlushRetryBackoff(current.Attempts));
                        }

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
                    state.SignalWritePressureChanged();
                    var identity = current.Frozen.ColumnFamily;
                    if (!state.IsShuttingDown &&
                        state.ActiveMemtableBytes.GetValueOrDefault(identity) >=
                        _options.MemtableFlushThresholdBytes)
                    {
                        _ = await FreezeAndScheduleFlushAsync(
                                state,
                                identity,
                                rejectWhenQueueFull: false)
                            .ConfigureAwait(false);
                    }

                    if (state.ActiveMemtableBytes.GetValueOrDefault(identity) == 0 &&
                        !state.ImmutableMemtableFlushes.Values.Any(flush =>
                            flush.Frozen.ColumnFamily == identity))
                    {
                        state.UnflushedFamilies.Remove(identity);
                    }

                    PublishSnapshot(state);
                    current.CompleteAttempt(failure: null);
                    if (!state.IsShuttingDown)
                    {
                        await ScheduleNextImmutableFlushAttemptAsync(
                                state,
                                retryFailure: false)
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
        }
        catch (Exception exception)
        {
            expected.CompleteAttempt(RuntimeExceptionMapper.ToPublicException(exception));
        }
    }

    async Task RetryImmutableFlushAfterDelayAsync(
        ImmutableMemtableFlush expected,
        int failedAttempt,
        TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
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
                            retryFailure: true)
                        .ConfigureAwait(false);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PantsAbortedException)
        {
        }
    }

    static TimeSpan GetFlushRetryBackoff(int attempts)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 7);
        var milliseconds = Math.Min(
            checked((long)InitialFlushRetryBackoff.TotalMilliseconds << exponent),
            checked((long)MaximumFlushRetryBackoff.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    static async ValueTask AwaitFlushAttemptsAsync(
        IReadOnlyList<Task<Exception?>> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var attempt in attempts)
        {
            var failure = await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    async ValueTask DrainImmutableFlushesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var attempts = await SendAsync<IReadOnlyList<Task<Exception?>>>(
                async state =>
                {
                    await ScheduleNextImmutableFlushAttemptAsync(state, retryFailure: true)
                        .ConfigureAwait(false);
                    var flushes = state.ImmutableMemtableFlushes.Values.ToArray();

                    return flushes.Select(static flush => flush.AttemptTask).ToArray();
                },
                cancellationToken).ConfigureAwait(false);
            if (attempts.Count == 0)
            {
                return;
            }

            await AwaitFlushAttemptsAsync(attempts, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask WaitForImmutableFlushWorkersAsync(CancellationToken cancellationToken)
    {
        var workers = await SendAsync<IReadOnlyList<Task>>(
            state => ValueTask.FromResult<IReadOnlyList<Task>>(
                state.ImmutableMemtableFlushes.Values
                    .Select(static flush => flush.RunningTask)
                    .Where(static task => task is not null)
                    .Cast<Task>()
                    .ToArray()),
            cancellationToken).ConfigureAwait(false);
        foreach (var worker in workers)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The public waiter was already failed at shutdown admission.
                // Worker quiescence, not its operation result, gates lease release.
            }
        }
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

                await ScheduleNextImmutableFlushAttemptAsync(state, retryFailure: true)
                    .ConfigureAwait(false);

                return state.ImmutableMemtableFlushes.Values
                    .Select(static flush => flush.AttemptTask)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        await AwaitFlushAttemptsAsync(attempts, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask FlushCloudSegmentGapAsync(PantsRuntimeState state)
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
            await _flushWorker.ExecuteAsync(() => _diskStore.Flush(state, identity))
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

    async ValueTask RunBackgroundCompactionAsync(PantsRuntimeState state)
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
                _diskStore.GetManifestSstNames(),
                CancellationToken.None)
            .ConfigureAwait(false);
        var result = default(CompactionResult);
        await _compactionWorker
            .ExecuteAsync(async workerCancellationToken =>
                result = await _diskStore.CompactAsync(
                        state,
                        force: false,
                        _cloudCompactionOutputPublisher,
                        flushMutableOperations: !UsesBackgroundImmutableFlushes,
                        workerCancellationToken)
                    .ConfigureAwait(false))
            .ConfigureAwait(false);

        if (result.PersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            MarkPersistenceAnomaly(state);
        }

        if (result.BytesRewritten > 0)
        {
            _telemetry.RecordCompaction(result.BytesRewritten);
        }

        if (result.BytesRewritten > 0 || _hybridCache is not null)
        {
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
    }

    async ValueTask RunReadAmplificationCompactionAsync(PantsRuntimeState state)
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
                _diskStore!.GetManifestSstNames(),
                CancellationToken.None)
            .ConfigureAwait(false);
        var result = default(CompactionResult);
        await _compactionWorker
            .ExecuteAsync(async workerCancellationToken =>
                result = await _diskStore!.CompactAsync(
                        state,
                        force: true,
                        _cloudCompactionOutputPublisher,
                        flushMutableOperations: !UsesBackgroundImmutableFlushes,
                        workerCancellationToken)
                    .ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!UsesBackgroundImmutableFlushes)
        {
            state.UnflushedFamilies.Clear();
            ClearMemtableAccounting(state);
        }

        if (result.PersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            MarkPersistenceAnomaly(state);
        }

        if (result.BytesRewritten > 0)
        {
            _telemetry.RecordCompaction(result.BytesRewritten);
        }

        if (result.BytesRewritten > 0 || _hybridCache is not null)
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
            _failpoints.Hit(PantsFailpoint.BeforeCompactionAdmission);
            _ = await SendAsync(
                async state =>
                {
                    if (state.IsShuttingDown ||
                        !_backgroundCompactionPending &&
                        !_readAmplificationCompactionPending)
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
                _failpoints.Hit(PantsFailpoint.BeforeDeferredCompactionSignalReset);
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
                await _flushWorker.ExecuteAsync(
                        () => _diskStore.Flush(state, identity),
                        cancellationToken)
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
        PantsRuntimeState state,
        ColumnFamilyIdentity identity)
    {
        state.UnflushedFamilies.Remove(identity);
        ClearMemtableAccounting(state, identity);
        _cloudMemtableSegments?.RecordFlush(identity);
    }

    async ValueTask MirrorCloudStorageAsync()
    {
        if (_cloudPersistence is null)
        {
            return;
        }

        ThrowIfVerificationInProgress();

        EnsureCloudWriteAuthorityValid();
        _telemetry.RecordCloudUploadPending();
        try
        {
            await _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync)
                .ConfigureAwait(false);
        }
        finally
        {
            _telemetry.RecordCloudUploadCompleted();
        }
        if (_cloudPersistence.HasPersistenceAnomaly)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            ApplyPendingPersistenceAnomaly(_state);
        }

        if (_hybridCache is not null && _diskStore is not null)
        {
            _hybridCache.EvictIfNeeded(
                _diskStore,
                _state.ActiveSnapshotCount != 0);
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

        await HybridCacheManager.EnsureLocalSstsAsync(
                _diskStore,
                names,
                FetchHybridSstAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask<ReadOnlyMemory<byte>?> FetchHybridSstAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var persistence = _cloudPersistence ??
            throw new PantsInternalException("Hybrid cache hydration has no cloud backend.");
        var result = (ReadOnlyMemory<byte>?)null;
        await _cloudWorker.ExecuteAsync(
                async workerCancellationToken =>
                {
                    result = await persistence.FetchSstAsync(name, workerCancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    static void RecordMemtableBytes(PantsRuntimeState state, CommitPayload payload)
    {
        foreach (var operations in
                 payload.OrderedOperations.GroupBy(
                     static operation => operation.Family,
                     ColumnFamilyIdentityComparer.Instance))
        {
            state.ActiveMemtableBytes[operations.Key] = checked(
                state.ActiveMemtableBytes.GetValueOrDefault(operations.Key) +
                operations.Sum(EstimateOperationBytes));
        }
    }

    void TrackCloudMemtableWrites(CommitPayload payload, ulong walSegmentId)
    {
        if (_cloudMemtableSegments is null)
        {
            return;
        }

        foreach (var family in payload.OrderedOperations
                     .Select(static operation => operation.Family)
                     .Distinct(ColumnFamilyIdentityComparer.Instance))
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

    bool IsCommitWriteStalled(PantsRuntimeState state, CommitPayload payload)
    {
        var families = GetCommitFamilies(payload);
        return families.Length != 0 &&
            MemtableWritePressure.IsStalled(_options, state, families);
    }

    static ColumnFamilyIdentity[] GetCommitFamilies(CommitPayload payload) =>
        payload.OrderedOperations
            .Select(static operation => operation.Family)
            .Concat(payload.Asserts.Keys)
            .Distinct(ColumnFamilyIdentityComparer.Instance)
            .ToArray();

    static void ClearMemtableAccounting(PantsRuntimeState state)
    {
        foreach (var identity in state.ActiveMemtableBytes.Keys.ToArray())
        {
            state.ActiveMemtableBytes[identity] = 0;
        }
    }

    static void ClearMemtableAccounting(
        PantsRuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.ActiveMemtableBytes[identity] = 0;

    static PantsStorageLayout EmptyStorageLayout(PantsRuntimeState state) => new(
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

    void PublishSnapshot(PantsRuntimeState state) =>
        Volatile.Write(ref _currentSnapshot, state.CreateSnapshot());

    void EnsureCloudLeaseValid() => _cloudLease?.EnsureValid();

    void EnsureCloudWriteAuthorityValid()
    {
        EnsureCloudLeaseValid();
        _cloudDdlCoordinator?.EnsureAuthorityResolved();
    }

    static void MarkPersistenceAnomaly(PantsRuntimeState state)
    {
        if (state.Health == PantsEngineHealth.Healthy)
        {
            state.Health = PantsEngineHealth.Degraded;
        }
    }

    void ApplyPendingPersistenceAnomaly(PantsRuntimeState state)
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

    static void ThrowIfShuttingDown(PantsRuntimeState state)
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

    async ValueTask CollectObsoleteFilesAfterSnapshotReleaseAsync(PantsRuntimeState state)
    {
        if (_diskStore is null)
        {
            return;
        }

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

    async ValueTask ReleaseVerificationBarrierAsync(OnlineVerificationBarrier barrier)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var maintenance = await SendAsync<OnlineVerificationMaintenance?>(
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
                                    retryFailure: true)
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
