using System.Diagnostics;
using System.Threading.Channels;

namespace Pants;

internal sealed class PantsActor : IAsyncDisposable
{
    private readonly PantsRuntimeState _state;
    private readonly PantsOpenOptions _options;
    private readonly Channel<IRuntimeCommand> _commands;
    private readonly RuntimeWorker _walWorker;
    private readonly RuntimeWorker _flushWorker;
    private readonly RuntimeWorker _compactionWorker;
    private readonly RuntimeWorker _manifestWorker;
    private readonly RuntimeWorker _garbageCollectionWorker;
    private readonly RuntimeWorker _cloudWorker;
    private readonly CancellationTokenSource _loopCancellation = new();
    private readonly Task _loopTask;
    private readonly LocalDiskStore? _diskStore;
    private readonly SimulatedCloudPersistence? _simulatedCloud;
    private readonly bool _cloudMode;
    private DatabaseSnapshot _currentSnapshot;
    private int _queuedCommands;
    private int _disposed;
    private bool _shutdownRequested;
    private long _compactionsRun;
    private long _writeConflicts;

    public PantsActor(PantsOpenOptions options, IPantsClock ttlClock)
    {
        _options = options;
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
                    leaseLossCallback: options.LeaseLossCallback);
                _cloudMode = false;
                break;
            case PantsStorageConfiguration.SimulatedCloud simulated:
                ulong minimumEpoch = SimulatedCloudPersistence.PrepareLocalCache(
                    simulated.LocalCachePath);
                _diskStore = LocalDiskStore.Open(
                    simulated.LocalCachePath,
                    _state,
                    minimumEpoch,
                    options.RecoveryPolicy,
                    options.PerformanceGoal,
                    options.LeaseClockSkewTolerance,
                    options.LeaseLossCallback);
                _simulatedCloud = new SimulatedCloudPersistence(
                    simulated.LocalCachePath,
                    _diskStore.WriterEpoch);
                _cloudMode = true;
                break;
            case PantsStorageConfiguration.Cloud:
                throw PantsException.Create(
                    PantsErrorCode.NotSupported,
                    "Provider-backed cloud storage is not available until its configured direct-HTTP client is qualified.");
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

    public bool IsPrimaryLeaseHealthy => _diskStore?.IsLeaseHealthy ?? true;

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
                if (state.ActiveFamilyVersions.ContainsKey(name))
                {
                    throw PantsException.InvalidArgument($"Column family '{name}' already exists.");
                }

                int generation = state.FamilyGeneration.TryGetValue(name, out int currentGeneration)
                    ? checked(currentGeneration + 1)
                    : 0;
                uint id = state.NextColumnFamilyId;
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
                if (_simulatedCloud is not null && _diskStore is not null)
                {
                    await _cloudWorker.ExecuteAsync(() =>
                    {
                        _simulatedCloud.PublishColumnFamilyCreate(
                            _diskStore.GetColumnFamilyMetadata(created));
                        _simulatedCloud.MirrorMetadataAndSsts();
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
                    if (_simulatedCloud is not null)
                    {
                        await _cloudWorker.ExecuteAsync(() =>
                        {
                            _simulatedCloud.PublishColumnFamilyDrop(
                                _diskStore.GetColumnFamilyMetadata(identity));
                            _simulatedCloud.MirrorMetadataAndSsts();
                        }).ConfigureAwait(false);
                    }
                }

                state.ActiveFamilyVersions.Remove(identity.Name);
                state.FamilyData.Remove(identity);
                state.RangeTombstones.Remove(identity);
                state.ActiveMemtableBytes.Remove(identity);
                state.UnflushedFamilies.Remove(identity);
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
                ColumnFamilyIdentity[] matches = state.FamilyData.Keys
                    .Where(candidate =>
                        candidate.Name == name &&
                        state.ActiveFamilyVersions.TryGetValue(name, out int generation) &&
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
                long snapshotId = checked(++state.TransactionCounter);
                state.ActiveScanSnapshots[snapshotId] = new ScanSnapshotPin(
                    snapshotId,
                    snapshot.Sequence,
                    state.Clock.UtcNow,
                    snapshot);
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
                    if (_diskStore is not null)
                    {
                        await _garbageCollectionWorker
                            .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                            .ConfigureAwait(false);
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
                ColumnFamilyIdentity identity = columnFamily.Identity;
                ValidateActiveFamily(state, identity);
                long transactionId = checked(++state.TransactionCounter);
                DatabaseSnapshot snapshot = state.CreateSnapshot();
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

        await SendAsync(
            async state =>
            {
                ThrowIfShuttingDown(state);
                if (!state.ActiveTransactions.Remove(payload.TransactionId))
                {
                    throw PantsException.Create(
                        PantsErrorCode.InvalidArgument,
                        $"Transaction {payload.TransactionId} is not active.");
                }

                if (_diskStore is not null)
                {
                    await _garbageCollectionWorker
                        .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                        .ConfigureAwait(false);
                }

                try
                {
                    ValidateCommit(state, payload);
                }
                catch (PantsException exception) when (exception.Code == PantsErrorCode.WriteConflict)
                {
                    _writeConflicts++;
                    PantsDiagnostics.TransactionsConflicted.Add(1);
                    throw;
                }

                if (payload.OrderedOperations.Count != 0)
                {
                    await RelieveWritePressureAsync(state, payload).ConfigureAwait(false);
                    if (_diskStore is null)
                    {
                        state.Sequence++;
                    }
                    else
                    {
                        await PersistCommitAsync(state, payload, writeOptions.Durability)
                            .ConfigureAwait(false);
                    }

                    ApplyOperations(state, payload, state.Sequence);
                    RecordMemtableBytes(state, payload);
                    foreach (ColumnFamilyIdentity family in payload.Writes.Keys.Concat(payload.DeleteRanges.Keys))
                    {
                        state.UnflushedFamilies.Add(family);
                    }

                    await FlushAtConfiguredThresholdAsync(state, payload).ConfigureAwait(false);
                }

                PublishSnapshot(state);
                PantsDiagnostics.TransactionsCommitted.Add(1);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RollbackAsync(long transactionId, CancellationToken cancellationToken)
    {
        await SendAsync(
            async state =>
            {
                if (state.ActiveTransactions.Remove(transactionId))
                {
                    PantsDiagnostics.TransactionsRolledBack.Add(1);
                    if (_diskStore is not null)
                    {
                        await _garbageCollectionWorker
                            .ExecuteAsync(() => _diskStore.CollectObsoleteFiles(state))
                            .ConfigureAwait(false);
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
                ValidateActiveFamily(state, identity);
                if (_diskStore is not null)
                {
                    await _flushWorker
                        .ExecuteAsync(() => _diskStore.Flush(state, identity))
                        .ConfigureAwait(false);
                }

                await MirrorCloudStorageAsync().ConfigureAwait(false);
                state.UnflushedFamilies.Clear();
                ClearMemtableAccounting(state);
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
                if (_diskStore is not null)
                {
                    await _compactionWorker
                        .ExecuteAsync(() => _diskStore.Compact(state))
                        .ConfigureAwait(false);
                }

                await MirrorCloudStorageAsync().ConfigureAwait(false);
                state.UnflushedFamilies.Clear();
                ClearMemtableAccounting(state);
                _compactionsRun++;
                PublishSnapshot(state);
                return true;
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

    private async ValueTask RelieveWritePressureAsync(
        PantsRuntimeState state,
        CommitPayload payload)
    {
        if (_diskStore is null)
        {
            return;
        }

        bool wouldExceedLimit = payload.OrderedOperations
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
                CompactionsRun = _compactionsRun,
                CompactionFailures = _compactionWorker.Failures,
                ObsoleteFileBacklog = _diskStore?.GetObsoleteFiles().Count ?? 0,
                WriteConflictsTotal = _writeConflicts,
                WalAppendCount = _diskStore?.WalRecords ?? 0,
                WalRecoveryRecordsReplayed = _diskStore?.WalRecoveryRecordsReplayed ?? 0,
                WalRecoveryBytesReplayed = _diskStore?.WalRecoveryBytesReplayed ?? 0,
                IntentLogReplayRuns = state.IntentLogReplayRuns,
                IntentLogEntriesReplayed = state.IntentLogEntriesReplayed,
                FlushQueueDepth = _flushWorker.QueueDepth,
                FlushInFlight = _flushWorker.InFlight,
                FlushEnqueuedTotal = _flushWorker.Enqueued,
                FlushFailuresTotal = _flushWorker.Failures,
                PendingEvictions = _garbageCollectionWorker.QueueDepth +
                    _garbageCollectionWorker.InFlight
            }),
            cancellationToken);

    public ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken) =>
        SendAsync(
            _ => ValueTask.FromResult(new PantsReadAmplificationMetrics(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0)),
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

        string? path = _diskStore?.RootPath;
        if (path is null)
        {
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
            return await PantsStorageVerifier.VerifyPathAsync(path, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw PantsException.Create(
                PantsErrorCode.Timeout,
                "Storage verification did not complete before its deadline.");
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
                    if (_simulatedCloud is not null)
                    {
                        SealedWalSegment? segment = null;
                        await _walWorker.ExecuteAsync(() => segment = _diskStore.SealActiveWal())
                            .ConfigureAwait(false);
                        if (segment is not null)
                        {
                            await _cloudWorker.ExecuteAsync(() => _simulatedCloud.PublishWal(segment))
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
        _diskStore?.Dispose();
        _loopCancellation.Dispose();
    }

    private async ValueTask<T> SendAsync<T>(
        Func<PantsRuntimeState, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is disposed.");
        }

        var command = new RuntimeCommand<T>(operation);
        Interlocked.Increment(ref _queuedCommands);
        long started = Stopwatch.GetTimestamp();
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

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (IRuntimeCommand command in _commands.Reader
                               .ReadAllAsync(_loopCancellation.Token)
                               .ConfigureAwait(false))
            {
                await command.ExecuteAsync(_state).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_loopCancellation.IsCancellationRequested)
        {
        }
    }

    private async ValueTask PersistCommitAsync(
        PantsRuntimeState state,
        CommitPayload payload,
        PantsDurability durability)
    {
        LocalDiskStore diskStore = _diskStore ??
            throw new PantsInternalException("Persistent commit has no disk store.");
        await _walWorker.ExecuteAsync(() => diskStore.AppendCommit(
                payload,
                state,
                durability is PantsDurability.CloudAsync or PantsDurability.CloudStrict
                    ? PantsDurability.Buffered
                    : durability))
            .ConfigureAwait(false);
        if (_options.FlushAfterWalRecords > 0 && diskStore.WalRecords >= _options.FlushAfterWalRecords)
        {
            await _flushWorker.ExecuteAsync(() => diskStore.Flush(state)).ConfigureAwait(false);
            await MirrorCloudStorageAsync().ConfigureAwait(false);
            state.UnflushedFamilies.Clear();
        }

        if (_simulatedCloud is not null && durability == PantsDurability.CloudAsync)
        {
            SealedWalSegment? asynchronousSegment = null;
            await _walWorker
                .ExecuteAsync(() => asynchronousSegment = diskStore.SealActiveWal())
                .ConfigureAwait(false);
            if (asynchronousSegment is not null)
            {
                await _cloudWorker
                    .EnqueueAsync(() => _simulatedCloud.PublishWal(asynchronousSegment))
                    .ConfigureAwait(false);
            }
        }
        else if (_simulatedCloud is not null && durability == PantsDurability.CloudStrict)
        {
            SealedWalSegment? segment = null;
            await _walWorker.ExecuteAsync(() => segment = diskStore.SealActiveWal())
                .ConfigureAwait(false);
            if (segment is not null)
            {
                await _cloudWorker.ExecuteAsync(() => _simulatedCloud.PublishWal(segment))
                    .ConfigureAwait(false);
            }
        }

    }

    private static void ValidateCommit(PantsRuntimeState state, CommitPayload payload)
    {
        DateTimeOffset now = state.Clock.UtcNow;
        ValidateInsertOnlyOperations(state, payload, now);
        foreach ((ColumnFamilyIdentity identity, IReadOnlyList<TransactionAssertion> assertions) in payload.Asserts)
        {
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> startFamily = payload.StartSnapshot.Families[identity];
            SortedDictionary<byte[], CellState> currentFamily = GetFamily(state, identity);
            foreach (TransactionAssertion assertion in assertions)
            {
                byte[] key = assertion.Key;
                TransactionReadValue expected = assertion.Expected;
                CellState? start = ResolveVisibleCell(startFamily, key, payload.SnapshotTime);
                if (!Matches(expected, start, payload.SnapshotTime))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A value assertion did not match the transaction's start snapshot.");
                }

                if (currentFamily.TryGetValue(key, out CellState? current) &&
                    current.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "An asserted key changed after the transaction began.");
                }

                if (state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "An asserted key was covered by a range deletion after the transaction began.");
                }
            }
        }

        foreach ((ColumnFamilyIdentity identity, Dictionary<byte[], TransactionPendingWrite> writes) in payload.Writes)
        {
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            foreach (byte[] key in writes.Keys)
            {
                if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict &&
                    family.TryGetValue(key, out CellState? rawCurrent) &&
                    rawCurrent.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A write-set key changed after the transaction began.");
                }

                if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict &&
                    state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A recent range deletion covers a write-set key.");
                }
            }
        }

        if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict)
        {
            foreach ((ColumnFamilyIdentity identity, List<DeleteRange> ranges) in payload.DeleteRanges)
            {
                ValidateActiveFamily(state, identity);
                SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
                foreach (DeleteRange range in ranges)
                {
                    if (family.Any(pair =>
                            IsInRange(pair.Key, range.Start, range.EndExclusive) &&
                            pair.Value.WriteSequence > payload.StartSnapshot.Sequence))
                    {
                        throw PantsException.Create(
                            PantsErrorCode.WriteConflict,
                            "A covered range changed after the transaction began.");
                    }
                    if (state.RangeTombstones[identity].Any(tombstone =>
                            tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                            RangesOverlap(
                                range.Start,
                                range.EndExclusive,
                                tombstone.Start,
                                tombstone.EndExclusive)))
                    {
                        throw PantsException.Create(
                            PantsErrorCode.WriteConflict,
                            "A covered range was deleted after the transaction began.");
                    }
                }
            }
        }
    }

    private static void ValidateInsertOnlyOperations(
        PantsRuntimeState state,
        CommitPayload payload,
        DateTimeOffset now)
    {
        foreach (IGrouping<ColumnFamilyIdentity, TransactionIntentOperation> familyOperations in
                 payload.OrderedOperations.GroupBy(
                     static operation => operation.Family,
                     ColumnFamilyIdentityComparer.Instance))
        {
            ColumnFamilyIdentity identity = familyOperations.Key;
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            var pointStates = new Dictionary<byte[], (ulong Ordinal, bool Exists)>(
                ByteArrayComparer.Instance);
            var rangeDeletes = new List<(ulong Ordinal, byte[] Start, byte[] EndExclusive)>();
            foreach (TransactionIntentOperation operation in familyOperations)
            {
                switch (operation.Kind)
                {
                    case CommitOperationKind.Put:
                        if (operation.InsertOnly && ResolvePriorExists(
                                operation.Key,
                                pointStates,
                                rangeDeletes,
                                family,
                                now))
                        {
                            throw PantsException.Create(
                                PantsErrorCode.WriteConflict,
                                "Insert requires an absent key.");
                        }

                        pointStates[operation.Key] = (operation.Ordinal, true);
                        break;
                    case CommitOperationKind.Delete:
                        pointStates[operation.Key] = (operation.Ordinal, false);
                        break;
                    case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                        rangeDeletes.Add((operation.Ordinal, operation.Key, operation.EndExclusive));
                        break;
                }
            }
        }
    }

    private static bool ResolvePriorExists(
        byte[] key,
        Dictionary<byte[], (ulong Ordinal, bool Exists)> pointStates,
        List<(ulong Ordinal, byte[] Start, byte[] EndExclusive)> rangeDeletes,
        SortedDictionary<byte[], CellState> family,
        DateTimeOffset now)
    {
        bool hasPrior = pointStates.TryGetValue(key, out (ulong Ordinal, bool Exists) pointState);
        ulong latestOrdinal = hasPrior ? pointState.Ordinal : 0;
        bool exists = hasPrior && pointState.Exists;
        foreach ((ulong ordinal, byte[] start, byte[] endExclusive) in rangeDeletes)
        {
            if (IsInRange(key, start, endExclusive) && (!hasPrior || ordinal > latestOrdinal))
            {
                hasPrior = true;
                latestOrdinal = ordinal;
                exists = false;
            }
        }

        return hasPrior
            ? exists
            : ResolveVisibleCell(family, key, now)?.Value is not null;
    }

    private static void ApplyOperations(PantsRuntimeState state, CommitPayload payload, long sequence)
    {
        foreach (TransactionIntentOperation operation in payload.OrderedOperations)
        {
            SortedDictionary<byte[], CellState> family = GetFamily(state, operation.Family);
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
                    foreach (byte[] key in family.Keys
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

    private static void ValidateActiveFamily(PantsRuntimeState state, ColumnFamilyIdentity identity)
    {
        if (!state.ActiveFamilyVersions.TryGetValue(identity.Name, out int activeGeneration) ||
            activeGeneration != identity.Generation ||
            !state.FamilyData.ContainsKey(identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

    private static SortedDictionary<byte[], CellState> GetFamily(
        PantsRuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.FamilyData.TryGetValue(identity, out SortedDictionary<byte[], CellState>? family)
            ? family
            : throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is unavailable.");

    private static CellState? ResolveVisibleCell(
        SortedDictionary<byte[], CellState> family,
        byte[] key,
        DateTimeOffset now) =>
        family.TryGetValue(key, out CellState? cell) && !cell.IsExpired(now) && cell.Value is not null
            ? cell
            : null;

    private static bool Matches(
        TransactionReadValue expected,
        CellState? actual,
        DateTimeOffset now)
    {
        if (expected.Missing)
        {
            return actual is null || actual.Value is null || actual.IsExpired(now);
        }

        return actual?.Value is { } value && value.AsSpan().SequenceEqual(expected.Value);
    }

    private static bool IsInRange(byte[] key, byte[] start, byte[] end) =>
        ByteArrayComparer.Instance.Compare(key, start) >= 0 &&
        ByteArrayComparer.Instance.Compare(key, end) < 0;

    private static bool RangesOverlap(
        byte[] leftStart,
        byte[] leftEnd,
        byte[] rightStart,
        byte[] rightEnd) =>
        ByteArrayComparer.Instance.Compare(leftStart, rightEnd) < 0 &&
        ByteArrayComparer.Instance.Compare(rightStart, leftEnd) < 0;

    private async ValueTask FlushAtConfiguredThresholdAsync(
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
    }

    private ValueTask MirrorCloudStorageAsync() =>
        _simulatedCloud is null
            ? ValueTask.CompletedTask
            : _cloudWorker.ExecuteAsync(_simulatedCloud.MirrorMetadataAndSsts);

    private static void RecordMemtableBytes(PantsRuntimeState state, CommitPayload payload)
    {
        foreach (IGrouping<ColumnFamilyIdentity, TransactionIntentOperation> operations in
                 payload.OrderedOperations.GroupBy(
                     static operation => operation.Family,
                     ColumnFamilyIdentityComparer.Instance))
        {
            state.ActiveMemtableBytes[operations.Key] = checked(
                state.ActiveMemtableBytes.GetValueOrDefault(operations.Key) +
                operations.Sum(EstimateOperationBytes));
        }
    }

    private static long EstimateOperationBytes(TransactionIntentOperation operation) => checked(
        (long)operation.Key.Length +
        (operation.EndExclusive?.Length ?? 0) +
        (operation.Value?.Length ?? 0) +
        64);

    private static void ClearMemtableAccounting(PantsRuntimeState state)
    {
        foreach (ColumnFamilyIdentity identity in state.ActiveMemtableBytes.Keys.ToArray())
        {
            state.ActiveMemtableBytes[identity] = 0;
        }
    }

    private static PantsStorageLayout EmptyStorageLayout(PantsRuntimeState state) => new(
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

    private static long GetOldestSnapshotAgeSeconds(PantsRuntimeState state) =>
        state.ActiveSnapshotCount == 0
            ? 0
            : checked((long)state.ActiveSnapshots
                .Max(snapshot => GetSnapshotAge(
                    state.Clock.UtcNow,
                    snapshot.StartedAtUtc).TotalSeconds));

    private static TimeSpan GetSnapshotAge(DateTimeOffset now, DateTimeOffset startedAtUtc) =>
        now <= startedAtUtc ? TimeSpan.Zero : now - startedAtUtc;

    private void PublishSnapshot(PantsRuntimeState state) =>
        Volatile.Write(ref _currentSnapshot, state.CreateSnapshot());

    private static void ThrowIfShuttingDown(PantsRuntimeState state)
    {
        if (state.IsShuttingDown)
        {
            throw PantsException.Create(PantsErrorCode.Aborted, "Pants database is shutting down.");
        }
    }
}
