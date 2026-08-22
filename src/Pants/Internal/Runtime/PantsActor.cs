using System.Diagnostics;
using System.Threading.Channels;

namespace Pants;

sealed class PantsActor : IAsyncDisposable
{
    readonly PantsRuntimeState _state;
    readonly PantsOpenOptions _options;
    readonly RuntimeTelemetry _telemetry;
    readonly PantsStorageVerificationDelegate _storageVerifier;
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
    readonly CloudLeaseCoordinator? _cloudLease;
    readonly CancellationTokenSource? _cloudLeaseCancellation;
    readonly Task? _cloudLeaseHeartbeat;
    readonly bool _cloudMode;
    DatabaseSnapshot _currentSnapshot;
    int _queuedCommands;
    int _disposed;
    bool _shutdownRequested;
    bool _verificationInProgress;
    bool _backgroundCompactionEnabled;

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
                var minimumEpoch = SimulatedCloudPersistence.PrepareLocalCache(
                    simulated.LocalCachePath);
                _diskStore = LocalDiskStore.Open(
                    simulated.LocalCachePath,
                    _state,
                    minimumEpoch,
                    options.RecoveryPolicy,
                    options.PerformanceGoal,
                    options.LeaseClockSkewTolerance,
                    options.LeaseLossCallback,
                    dependencies.Failpoints,
                    options.Compaction,
                    options.TargetSstSizeBytes,
                    options.BlockCachePolicy,
                    options.BlockCacheBytes,
                    dependencies.LeaseHeartbeatInterval);
                _cloudPersistence = new SimulatedCloudPersistence(
                    simulated.LocalCachePath,
                    _diskStore.WriterEpoch);
                _cloudMode = true;
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
                _cloudLease = new CloudLeaseCoordinator(
                    new CloudObjectLeaseStore(controlStore, PantsCloudObjectLayout.LeaseObjectKey),
                    ttlClock,
                    $"pants-{Environment.ProcessId}-{Guid.NewGuid():N}",
                    TimeSpan.FromSeconds(30),
                    options.LeaseClockSkewTolerance,
                    options.LeaseLossCallback);
                var cloudEpoch = _cloudLease.AcquireAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                ProviderCloudPersistence.HydrateLocalCacheAsync(
                    cloud.LocalCachePath,
                    walStore,
                    sstStore,
                    controlStore,
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
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
                    dependencies.LeaseHeartbeatInterval);
                _cloudPersistence = new ProviderCloudPersistence(
                    cloud.LocalCachePath,
                    walStore,
                    sstStore,
                    controlStore,
                    _cloudLease);
                _cloudLeaseCancellation = new CancellationTokenSource();
                _cloudLeaseHeartbeat = RunCloudLeaseHeartbeatAsync(
                    _cloudLease,
                    dependencies.LeaseHeartbeatInterval,
                    _cloudLeaseCancellation.Token);
                _cloudMode = true;
                break;
            default:
                throw PantsException.Create(PantsErrorCode.NotSupported, "Unknown storage backend.");
        }

        _walWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
        _flushWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
        _compactionWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
        _manifestWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
        _garbageCollectionWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
        _cloudWorker = new RuntimeWorker(options.CoordinatorQueueCapacity);
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
                if (_diskStore is not null)
                {
                    await _manifestWorker
                        .ExecuteAsync(() => _diskStore.CreateColumnFamily(created))
                        .ConfigureAwait(false);
                }

                state.NextColumnFamilyId = checked(id + 1);
                state.FamilyGeneration[name] = generation;
                state.ActiveFamilyVersions[name] = generation;
                state.FamilyData[created] = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
                state.RangeTombstones[created] = [];
                state.ActiveMemtableBytes[created] = 0;
                if (_cloudPersistence is not null && _diskStore is not null)
                {
                    await _cloudWorker.ExecuteAsync(async workerCancellationToken =>
                    {
                        await _cloudPersistence.PublishColumnFamilyCreateAsync(
                            _diskStore.GetColumnFamilyMetadata(created),
                            workerCancellationToken).ConfigureAwait(false);
                        await _cloudPersistence.MirrorMetadataAndSstsAsync(workerCancellationToken)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
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
        await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                ValidateActiveFamily(state, identity);
                if (!discardUnflushed && state.UnflushedFamilies.Contains(identity))
                {
                    throw PantsException.Create(
                        PantsErrorCode.Busy,
                        $"Column family '{identity.Name}' has committed data that has not been flushed.");
                }

                if (_diskStore is not null)
                {
                    await _manifestWorker
                        .ExecuteAsync(() => _diskStore.DropColumnFamily(state, identity))
                        .ConfigureAwait(false);
                    if (_cloudPersistence is not null)
                    {
                        await _cloudWorker.ExecuteAsync(async workerCancellationToken =>
                        {
                            await _cloudPersistence.PublishColumnFamilyDropAsync(
                                _diskStore.GetColumnFamilyMetadata(identity),
                                workerCancellationToken).ConfigureAwait(false);
                            await _cloudPersistence.MirrorMetadataAndSstsAsync(workerCancellationToken)
                                .ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    }
                }

                state.ActiveFamilyVersions.Remove(identity.Name);
                state.FamilyData.Remove(identity);
                state.RangeTombstones.Remove(identity);
                state.ActiveMemtableBytes.Remove(identity);
                state.UnflushedFamilies.Remove(identity);
                if (_diskStore is not null)
                {
                    await _garbageCollectionWorker
                        .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                        .ConfigureAwait(false);
                    if (_cloudPersistence is not null)
                    {
                        await _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync)
                            .ConfigureAwait(false);
                    }
                }

                PublishSnapshot(state);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
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
                    if (_diskStore is not null)
                    {
                        await _garbageCollectionWorker
                            .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                            .ConfigureAwait(false);
                        if (_cloudPersistence is not null)
                        {
                            await _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync)
                                .ConfigureAwait(false);
                        }
                    }
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
                    if (_diskStore is not null)
                    {
                        await _garbageCollectionWorker
                            .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                            .ConfigureAwait(false);
                        if (_cloudPersistence is not null)
                        {
                            await _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync)
                                .ConfigureAwait(false);
                        }
                    }
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FlushAsync(
        ColumnFamilyIdentity identity,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                ValidateActiveFamily(state, identity);
                if (_diskStore is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    await _flushWorker
                        .ExecuteAsync(() => _diskStore.Flush(state, identity))
                        .ConfigureAwait(false);
                    _telemetry.RecordFlush(Stopwatch.GetElapsedTime(started));
                }

                await MirrorCloudStorageAsync().ConfigureAwait(false);
                state.UnflushedFamilies.Remove(identity);
                ClearMemtableAccounting(state, identity);
                await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
                PublishSnapshot(state);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompactAsync(CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                ThrowIfVerificationInProgress();
                if (_diskStore is not null)
                {
                    long bytesRewritten = 0;
                    await _compactionWorker
                        .ExecuteAsync(() => bytesRewritten = _diskStore.Compact(state, force: true))
                        .ConfigureAwait(false);
                    if (bytesRewritten > 0)
                    {
                        _telemetry.RecordCompaction(bytesRewritten);
                    }
                }

                await MirrorCloudStorageAsync().ConfigureAwait(false);
                state.UnflushedFamilies.Clear();
                ClearMemtableAccounting(state);
                PublishSnapshot(state);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetBackgroundCompactionAsync(bool enabled, CancellationToken cancellationToken)
    {
        await SendAsync(
            state =>
            {
                ThrowIfShuttingDown(state);
                _backgroundCompactionEnabled = enabled;
                return ValueTask.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> WaitForWriteStallClearAsync(
        ColumnFamilyIdentity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(
            state =>
            {
                ValidateActiveFamily(state, identity);
                if (timeout < TimeSpan.Zero)
                {
                    throw PantsException.InvalidArgument("Write-stall timeout must not be negative.");
                }

                return ValueTask.FromResult(true);
            },
            cancellationToken);

    async ValueTask RelieveWritePressureAsync(
        PantsRuntimeState state,
        CommitPayload payload)
    {
        if (_diskStore is null)
        {
            return;
        }

        var wouldExceedLimit = payload.OrderedOperations
            .GroupBy(static operation => operation.Family, ColumnFamilyIdentityComparer.Instance)
            .Any(group =>
                state.ActiveMemtableBytes.GetValueOrDefault(group.Key) > 0 &&
                state.ActiveMemtableBytes.GetValueOrDefault(group.Key) +
                group.Sum(EstimateOperationBytes) > _options.MemtableSizeLimitBytes);
        if (!wouldExceedLimit)
        {
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
            state => ValueTask.FromResult(new PantsRuntimeMetrics
            {
                Health = _diskStore?.GetHealth(state) ?? state.Health,
                CurrentSequence = state.Sequence,
                ManifestLastPersistedSequence = _diskStore?.LastPersistedSequence ?? 0,
                ManifestNextWalSequence = _diskStore?.NextWalSequence ?? 1,
                ActiveMemtables = state.FamilyData.Count,
                TotalMemtableBytes = state.ActiveMemtableBytes.Values.Sum(),
                MemtableSizeLimitBytes = _options.MemtableSizeLimitBytes,
                MemtableFlushThresholdBytes = _options.MemtableFlushThresholdBytes,
                WalPendingWrites = _walWorker.QueueDepth + _walWorker.InFlight,
                PendingCompactions = _compactionWorker.QueueDepth,
                ActiveCompactions = _compactionWorker.InFlight,
                PendingCloudUploads = _cloudWorker.QueueDepth + _cloudWorker.InFlight,
                ActiveSnapshots = state.ActiveSnapshotCount,
                PinnedSsts = state.ActiveSnapshotCount == 0 ? 0 : _diskStore?.SstCount ?? 0,
                OldestSnapshotAgeSeconds = GetOldestSnapshotAgeSeconds(state),
                SstCount = _diskStore?.SstCount ?? 0,
                SstBytes = _diskStore?.SstBytes ?? 0,
                SalvageModeOpens = state.SalvageModeOpens,
                NoSpaceEvents = state.NoSpaceEvents,
                CompactionsRun = _telemetry.CompactionsRun,
                CompactionBytesRewritten = _telemetry.CompactionBytesRewritten,
                CompactionFailures = _compactionWorker.Failures,
                ObsoleteFileBacklog = _diskStore?.GetObsoleteFiles().Count ?? 0,
                WriteConflictsTotal = checked(
                    _telemetry.WriteConflictsPointTotal + _telemetry.WriteConflictsRangeTotal),
                WriteConflictsPointTotal = _telemetry.WriteConflictsPointTotal,
                WriteConflictsRangeTotal = _telemetry.WriteConflictsRangeTotal,
                CacheHits = _telemetry.CacheHits,
                CacheMisses = _telemetry.CacheMisses,
                WalAppendCount = _telemetry.WalAppendCount,
                WalFlushCount = _telemetry.WalFlushCount,
                WalFsyncCount = _telemetry.WalFsyncCount,
                WalAppendNanosecondsTotal = _telemetry.WalAppendNanosecondsTotal,
                WalFsyncNanosecondsTotal = _telemetry.WalFsyncNanosecondsTotal,
                WalFsyncNanosecondsMaximum = _telemetry.WalFsyncNanosecondsMaximum,
                DurabilityWaitersFannedOutTotal = _telemetry.DurabilityWaitersFannedOut,
                SstBloomRejectsTotal = _telemetry.SstBloomRejects,
                SstBloomChecksTotal = _telemetry.SstBloomChecks,
                SstBloomTruePositivesTotal = _telemetry.SstBloomTruePositives,
                SstBloomFalsePositivesTotal = _telemetry.SstBloomFalsePositives,
                SstKeyRangeRejectsTotal = _telemetry.SstKeyRangeRejects,
                SstDataBlocksReadTotal = _telemetry.SstDataBlocksRead,
                ReadAmplificationCompactionTriggersTotal =
                    _telemetry.ReadAmplificationCompactionTriggers,
                WalRecoveryRecordsReplayed = _diskStore?.WalRecoveryRecordsReplayed ?? 0,
                WalRecoveryBytesReplayed = _diskStore?.WalRecoveryBytesReplayed ?? 0,
                IntentLogReplayRuns = state.IntentLogReplayRuns,
                IntentLogEntriesReplayed = state.IntentLogEntriesReplayed,
                FlushQueueDepth = _flushWorker.QueueDepth,
                FlushInFlight = _flushWorker.InFlight,
                FlushEnqueuedTotal = _flushWorker.Enqueued,
                FlushBuildCount = _telemetry.FlushBuildCount,
                FlushBuildNanosecondsTotal = _telemetry.FlushBuildNanosecondsTotal,
                FlushBuildNanosecondsMaximum = _telemetry.FlushBuildNanosecondsMaximum,
                FlushPublishCount = _telemetry.FlushPublishCount,
                FlushPublishNanosecondsTotal = _telemetry.FlushPublishNanosecondsTotal,
                FlushPublishNanosecondsMaximum = _telemetry.FlushPublishNanosecondsMaximum,
                FlushFailuresTotal = _flushWorker.Failures,
                HybridPendingEvictions = _garbageCollectionWorker.QueueDepth +
                    _garbageCollectionWorker.InFlight
            }),
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
            _ =>
            {
                var validator = _diskStore?.CreateScanReadValidator(
                    _telemetry,
                    columnFamily,
                    bounds);
                return ValueTask.FromResult(validator);
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
            state => ValueTask.FromResult(
                _diskStore?.GetStorageLayout(state) ?? EmptyStorageLayout(state)),
            cancellationToken);

    public async ValueTask<PantsStorageVerificationReport> VerifyStorageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Verification timeout must be greater than zero.");
        }

        var path = await SendAsync(
            _ =>
            {
                if (_verificationInProgress)
                {
                    throw new PantsBusyException("Storage verification is already in progress.");
                }

                _verificationInProgress = true;
                return ValueTask.FromResult(_diskStore?.RootPath);
            },
            cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            await ReleaseVerificationBarrierAsync().ConfigureAwait(false);
            throw PantsException.Create(
                PantsErrorCode.NotSupported,
                "In-memory storage has no persistent path to verify.");
        }

        using CancellationTokenSource deadline = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await _storageVerifier(path, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw PantsException.Create(
                PantsErrorCode.Timeout,
                "Storage verification did not complete before its deadline.");
        }
        finally
        {
            await ReleaseVerificationBarrierAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                if (_shutdownRequested)
                {
                    return true;
                }

                ThrowIfVerificationInProgress();

                if (state.ActiveSnapshotCount != 0)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Busy,
                        "Database shutdown is blocked by active transactions or scans.");
                }

                _shutdownRequested = true;
                state.IsShuttingDown = true;
                state.ActiveTransactions.Clear();
                state.ActiveScanSnapshots.Clear();
                if (_diskStore is not null)
                {
                    await _walWorker.ExecuteAsync(_diskStore.FlushDurabilityBoundary)
                        .ConfigureAwait(false);
                    if (_cloudPersistence is not null)
                    {
                        SealedWalSegment? segment = null;
                        await _walWorker.ExecuteAsync(() => segment = _diskStore.SealActiveWal())
                            .ConfigureAwait(false);
                        if (segment is not null)
                        {
                            await _cloudWorker.ExecuteAsync(cancellation =>
                                    _cloudPersistence.PublishWalAsync(segment, cancellation))
                                .ConfigureAwait(false);
                        }
                    }
                }

                await MirrorCloudStorageAsync().ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        await _loopTask.ConfigureAwait(false);
        await _walWorker.DisposeAsync().ConfigureAwait(false);
        await _flushWorker.DisposeAsync().ConfigureAwait(false);
        await _compactionWorker.DisposeAsync().ConfigureAwait(false);
        await _manifestWorker.DisposeAsync().ConfigureAwait(false);
        await _garbageCollectionWorker.DisposeAsync().ConfigureAwait(false);
        await _cloudWorker.DisposeAsync().ConfigureAwait(false);
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
            _ = await command.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PantsDiagnostics.CommandsRejected.Add(1);
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
                    PantsDurability.Buffered);
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
                accepted.Count);
            foreach (var command in accepted)
            {
                await FlushAtConfiguredThresholdAsync(state, command.Payload).ConfigureAwait(false);
                PantsDiagnostics.TransactionsCommitted.Add(1);
            }

            await RotateLocalWalAtConfiguredThresholdAsync(diskStore).ConfigureAwait(false);
            PublishSnapshot(state);
            foreach (var command in accepted)
            {
                command.Complete(true);
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
        await PrepareCommitAsync(state, payload).ConfigureAwait(false);
        if (payload.OrderedOperations.Count != 0)
        {
            if (_diskStore is null)
            {
                state.Sequence++;
            }
            else
            {
                await PersistCommitAsync(state, payload, writeOptions.Durability)
                    .ConfigureAwait(false);
            }

            ApplyCommittedOperations(state, payload);
            await FlushAtConfiguredThresholdAsync(state, payload).ConfigureAwait(false);
        }

        PublishSnapshot(state);
        PantsDiagnostics.TransactionsCommitted.Add(1);
        return true;
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
            await _garbageCollectionWorker
                .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                .ConfigureAwait(false);
            if (_cloudPersistence is not null)
            {
                await _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync)
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
            _telemetry.RecordWalAppend(Stopwatch.GetElapsedTime(started), durability);
        }
        if (_options.FlushAfterWalRecords > 0 && diskStore.WalRecords >= _options.FlushAfterWalRecords)
        {
            await _flushWorker.ExecuteAsync(() => diskStore.Flush(state)).ConfigureAwait(false);
            await MirrorCloudStorageAsync().ConfigureAwait(false);
            state.UnflushedFamilies.Clear();
            await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
        }

        await RotateLocalWalAtConfiguredThresholdAsync(diskStore).ConfigureAwait(false);

        if (_cloudPersistence is not null && durability == PantsDurability.CloudAsync)
        {
            SealedWalSegment? asynchronousSegment = null;
            await _walWorker
                .ExecuteAsync(() => asynchronousSegment = diskStore.SealActiveWal())
                .ConfigureAwait(false);
            if (asynchronousSegment is not null)
            {
                await _cloudWorker
                    .EnqueueAsync(cancellation =>
                        _cloudPersistence.PublishWalAsync(asynchronousSegment, cancellation))
                    .ConfigureAwait(false);
            }
        }
        else if (_cloudPersistence is not null && durability == PantsDurability.CloudStrict)
        {
            SealedWalSegment? segment = null;
            await _walWorker.ExecuteAsync(() => segment = diskStore.SealActiveWal())
                .ConfigureAwait(false);
            if (segment is not null)
            {
                await _cloudWorker.ExecuteAsync(cancellation =>
                        _cloudPersistence.PublishWalAsync(segment, cancellation))
                    .ConfigureAwait(false);
            }
        }

    }

    async ValueTask RotateLocalWalAtConfiguredThresholdAsync(LocalDiskStore diskStore)
    {
        if (_options.Storage is not PantsStorageConfiguration.Local ||
            diskStore.ActiveWalBytes < _options.WalBufferSizeBytes)
        {
            return;
        }

        await _walWorker.ExecuteAsync(() =>
        {
            _ = diskStore.SealActiveWal();
        }).ConfigureAwait(false);
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
        if (!state.ActiveFamilyVersions.TryGetValue(identity.Name, out var activeGeneration) ||
            activeGeneration != identity.Generation ||
            !state.FamilyData.ContainsKey(identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

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
        if (_diskStore is null ||
            !payload.OrderedOperations.Any(operation =>
                state.ActiveMemtableBytes.GetValueOrDefault(operation.Family) >=
                _options.MemtableFlushThresholdBytes))
        {
            return;
        }

        await _flushWorker.ExecuteAsync(() => _diskStore.Flush(state)).ConfigureAwait(false);
        await MirrorCloudStorageAsync().ConfigureAwait(false);
        state.UnflushedFamilies.Clear();
        ClearMemtableAccounting(state);
        await RunBackgroundCompactionAsync(state).ConfigureAwait(false);
    }

    async ValueTask RunBackgroundCompactionAsync(PantsRuntimeState state)
    {
        if (!_backgroundCompactionEnabled || _diskStore is null)
        {
            return;
        }

        long bytesRewritten = 0;
        await _compactionWorker
            .ExecuteAsync(() => bytesRewritten = _diskStore.Compact(state, force: false))
            .ConfigureAwait(false);
        if (bytesRewritten > 0)
        {
            _telemetry.RecordCompaction(bytesRewritten);
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
    }

    async ValueTask RunReadAmplificationCompactionAsync(PantsRuntimeState state)
    {
        _telemetry.RecordReadAmplificationCompactionTrigger();
        long bytesRewritten = 0;
        await _compactionWorker
            .ExecuteAsync(() => bytesRewritten = _diskStore!.Compact(state, force: true))
            .ConfigureAwait(false);
        state.UnflushedFamilies.Clear();
        ClearMemtableAccounting(state);
        if (bytesRewritten > 0)
        {
            _telemetry.RecordCompaction(bytesRewritten);
            await MirrorCloudStorageAsync().ConfigureAwait(false);
        }
    }

    ValueTask MirrorCloudStorageAsync() =>
        _cloudPersistence is null
            ? ValueTask.CompletedTask
            : _cloudWorker.ExecuteAsync(_cloudPersistence.MirrorMetadataAndSstsAsync);

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

    static long EstimateOperationBytes(TransactionIntentOperation operation) => checked(
        (long)operation.Key.Length +
        (operation.EndExclusive?.Length ?? 0) +
        (operation.Value?.Length ?? 0) +
        64);

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

    static long GetOldestSnapshotAgeSeconds(PantsRuntimeState state) =>
        state.ActiveSnapshotCount == 0
            ? 0
            : checked((long)state.ActiveSnapshots
                .Max(snapshot => GetSnapshotAge(
                    state.Clock.UtcNow,
                    snapshot.StartedAtUtc).TotalSeconds));

    static TimeSpan GetSnapshotAge(DateTimeOffset now, DateTimeOffset startedAtUtc) =>
        now <= startedAtUtc ? TimeSpan.Zero : now - startedAtUtc;

    void PublishSnapshot(PantsRuntimeState state) =>
        Volatile.Write(ref _currentSnapshot, state.CreateSnapshot());

    static void ThrowIfShuttingDown(PantsRuntimeState state)
    {
        if (state.IsShuttingDown)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is shutting down.");
        }
    }

    void ThrowIfVerificationInProgress()
    {
        if (_verificationInProgress)
        {
            throw new PantsBusyException(
                "The storage layout is pinned by online verification.");
        }
    }

    async ValueTask ReleaseVerificationBarrierAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _ = await SendAsync(
                _ =>
                {
                    _verificationInProgress = false;
                    return ValueTask.FromResult(true);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PantsAbortedException)
        {
            // Disposal owns the remaining lease and filesystem lifetime.
        }
    }
}
