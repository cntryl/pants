using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cntryl.Pants.Storage.Internal;

sealed class LocalDiskStore :
    IDisposable,
    ILocalWalStore,
    ILocalFlushStore,
    ILocalCompactionStore,
    IStorageReadStore,
    IHybridCacheStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Midge's current writer names sealed segments "{segmentId:00000000000000000000}.wal".
    // Older writers instead used "wal_{segmentId:000000}.log". A reader must still recognize
    // that legacy shape on decode, though the current writer never emits it.
    static readonly Regex LegacyWalSegmentFileNameRegex =
        new(@"^wal_(\d{6})\.log$", RegexOptions.Compiled);

    readonly SstBlockCache _blockCache;
    readonly PantsCompactionConfiguration _compaction;
    readonly IFailpointHandler _failpoints;
    readonly Dictionary<ColumnFamilyIdentity, uint> _familyIds = new(ColumnFamilyIdentityComparer.Instance);
    readonly HashSet<long> _frozenFlushIds = [];
    readonly string _intentPath;
    readonly FileLease _lease;
    readonly FileStream _lockStream;
    readonly ManifestState _manifest;
    readonly object _manifestGate = new();
    readonly string _manifestJournalPath;
    readonly string _manifestPath;
    readonly string _manifestSnapshotPath;
    readonly MutableMemtableOperations _mutableOperations = new();
    readonly PantsPerformanceGoal _performanceGoal;
    readonly SstReaderCache _readerCache = new();
    readonly PantsRecoveryPolicy _recoveryPolicy;
    readonly IAsyncSstSourceFactory? _remoteSstSourceFactory;
    readonly Dictionary<uint, ulong> _reservedFlushSstSequences = [];

    readonly HashSet<string> _snapshotPinnedObsoleteFiles = new(StringComparer.Ordinal);
    readonly string _sstDirectory;
    readonly long _targetSstSizeBytes;
    readonly string _walDirectory;
    readonly string _walPath;
    readonly object _walStateGate = new();
    ManifestReadSnapshot _manifestReadSnapshot;
    long _nextFrozenFlushId;
    ulong _nextSequence;
    long _sstBytesWrittenTotal;
    ulong _unflushedCommitSequence;
    long _walBytesWrittenTotal;
    long _walLastAppendedSequence;
    long _walLastSyncedSequence;
    long _walLocalDurableSequence;
    int _walPendingWrites;
    int _walRecords;
    FileStream _walStream;
    Exception? _walWriteFailure;

    LocalDiskStore(
        string root,
        FileStream lockStream,
        FileLease lease,
        FileStream walStream,
        ManifestState manifest,
        PantsRecoveryPolicy recoveryPolicy,
        PantsPerformanceGoal performanceGoal,
        IFailpointHandler failpoints,
        PantsCompactionConfiguration compaction,
        long targetSstSizeBytes,
        PantsBlockCachePolicy blockCachePolicy,
        long blockCacheBytes,
        IAsyncSstSourceFactory? remoteSstSourceFactory)
    {
        RootPath = root;
        _walDirectory = Path.Combine(root, "wal");
        _sstDirectory = Path.Combine(root, "sst");
        _walPath = Path.Combine(_walDirectory, "wal.log");
        _manifestPath = Path.Combine(root, "manifest.json");
        _manifestSnapshotPath = Path.Combine(root, "manifest.snapshot.json");
        _manifestJournalPath = Path.Combine(root, "manifest.journal");
        _intentPath = Path.Combine(root, "intent_log.json");
        _lockStream = lockStream;
        _lease = lease;
        _recoveryPolicy = recoveryPolicy;
        _performanceGoal = performanceGoal;
        _failpoints = failpoints;
        _compaction = compaction;
        _targetSstSizeBytes = targetSstSizeBytes;
        _blockCache = new SstBlockCache(blockCachePolicy, blockCacheBytes);
        _remoteSstSourceFactory = remoteSstSourceFactory;
        BlockCacheCapacityBytes = blockCacheBytes;
        _walStream = walStream;
        _manifest = manifest;
        var visibleSequenceFloor = GetManifestVisibleSequenceFloor(manifest);
        _manifest.LastPersistedSequence = Math.Max(
            _manifest.LastPersistedSequence,
            visibleSequenceFloor);
        _manifestReadSnapshot = ManifestReadSnapshot.Create(manifest);
        _nextSequence = visibleSequenceFloor;
        _unflushedCommitSequence = visibleSequenceFloor;
    }

    public int WalRecords
    {
        get
        {
            lock (_walStateGate)
            {
                return _walRecords;
            }
        }
    }

    public int WalPendingWrites
    {
        get
        {
            lock (_walStateGate)
            {
                return _walPendingWrites;
            }
        }
    }

    public long WalLastSyncedSequence
    {
        get
        {
            lock (_walStateGate)
            {
                return _walLastSyncedSequence;
            }
        }
    }

    public long WalLocalDurableSequence
    {
        get
        {
            lock (_walStateGate)
            {
                return Math.Max(
                    _walLocalDurableSequence,
                    LastPersistedSequence);
            }
        }
    }

    public long ActiveWalBytes
    {
        get
        {
            lock (_walStateGate)
            {
                return _walStream.Length;
            }
        }
    }

    public string RootPath { get; }

    internal bool IsDisposed { get; private set; }

    public bool IsLeaseHealthy
    {
        get
        {
            try
            {
                _lease.EnsureValid();
                return true;
            }
            catch (PantsException)
            {
                return false;
            }
        }
    }

    public long LastPersistedSequence
        => checked((long)Volatile.Read(ref _manifestReadSnapshot).LastPersistedSequence);

    public long NextWalSequence
        => checked((long)Volatile.Read(ref _manifestReadSnapshot).NextWalSequence);

    public ulong CurrentWalSegmentId
        => Volatile.Read(ref _manifestReadSnapshot).NextWalSequence;

    public int SstCount => GetManifestFilesSnapshot().Length;

    public int SnapshotPinnedObsoleteFileCount => _snapshotPinnedObsoleteFiles.Count;

    public long SstBytes => checked((long)GetManifestFilesSnapshot().Aggregate(
        0UL,
        static (total, file) => total + file.SizeBytes));

    public long BlockCacheUsedBytes => _blockCache.UsedBytes;

    public long BlockCacheCapacityBytes { get; }

    public long WalBytesWrittenTotal => Interlocked.Read(ref _walBytesWrittenTotal);

    public long SstBytesWrittenTotal => Interlocked.Read(ref _sstBytesWrittenTotal);

    public long LocalWalBytes
    {
        get
        {
            lock (_walStateGate)
            {
                return checked(
                    GetLocalFileBytes(EnumerateSealedWalSegmentPaths(_walDirectory)) +
                    GetExistingFileBytes(_walPath));
            }
        }
    }

    public long LocalSstBytes => GetLocalFileBytes(_sstDirectory, "*.sst");
    public ulong WriterEpoch => _lease.Epoch;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _readerCache.Dispose();
        _walStream.Dispose();
        _lease.Dispose();
        _lockStream.Dispose();
    }

    public long LocalCommittedBytes => checked(LocalWalBytes + LocalSstBytes);

    public IReadOnlyList<HybridLocalSst> GetLocalManifestSsts() =>
        GetManifestFilesSnapshot()
            .OrderBy(static file => file.SstSequence)
            .ThenBy(static file => file.Name, StringComparer.Ordinal)
            .Where(file => File.Exists(Path.Combine(_sstDirectory, file.Name)))
            .Select(file => new HybridLocalSst(
                file.Name,
                new FileInfo(Path.Combine(_sstDirectory, file.Name)).Length))
            .ToArray();

    public async ValueTask VerifyRemoteSstMatchesLocalAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var metadata = GetManifestSst(name);
        var path = Path.Combine(_sstDirectory, name);
        if (!File.Exists(path))
        {
            throw new PantsRecoveryFailedException(
                $"Hybrid SST '{name}' disappeared before cloud durability could be verified.");
        }

        var factory = _remoteSstSourceFactory ??
                      throw new PantsInternalException(
                          "Hybrid SST verification requires a remote source factory.");
        await using var local = LocalAsyncSstSource.Open(path);
        await using var remote = await factory.OpenAsync(metadata, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
            $"Hybrid SST '{name}' is not confirmed durable in cloud storage.");
        if (local.Length != remote.Length)
        {
            throw new PantsRecoveryFailedException(
                $"Hybrid SST '{name}' differs from its cloud copy.");
        }

        const int verificationChunkBytes = 64 * 1024;
        for (long offset = 0; offset < local.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = checked((int)Math.Min(verificationChunkBytes, local.Length - offset));
            var localBytes = await local.ReadExactlyAsync(offset, length, cancellationToken)
                .ConfigureAwait(false);
            var remoteBytes = await remote.ReadExactlyAsync(offset, length, cancellationToken)
                .ConfigureAwait(false);
            if (!localBytes.AsSpan().SequenceEqual(remoteBytes))
            {
                throw new PantsRecoveryFailedException(
                    $"Hybrid SST '{name}' differs from its cloud copy.");
            }

            offset = checked(offset + length);
        }
    }

    public bool IsSstLocal(string name)
    {
        _ = GetManifestSst(name);
        return File.Exists(Path.Combine(_sstDirectory, name));
    }

    public async ValueTask HydrateLocalSstAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        var metadata = GetManifestSst(name);
        var path = Path.Combine(_sstDirectory, name);
        if (File.Exists(path))
        {
            return;
        }

        var factory = _remoteSstSourceFactory ??
                      throw new PantsInternalException(
                          "Remote SST hydration requires a remote source factory.");
        await using var source = await factory.OpenAsync(metadata, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
            $"Manifest-owned cloud SST '{name}' is missing during cache hydration.");
        if (checked((ulong)source.Length) != metadata.SizeBytes)
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{name}' length differs from its manifest.");
        }

        var stagingDirectory = Path.Combine(_sstDirectory, ".flush-staging");
        var stagingPath = Path.Combine(
            stagingDirectory,
            $"{_lease.Epoch}.hydration.{name}.{Guid.NewGuid():N}.tmp");
        var checksum = 0U;
        long offset = 0;
        try
        {
            await using (var output = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (offset < source.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = checked((int)Math.Min(64 * 1024, source.Length - offset));
                    var bytes = await source.ReadExactlyAsync(offset, length, cancellationToken)
                        .ConfigureAwait(false);
                    checksum = DiskFormat.Crc32CAppend(checksum, bytes);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    offset = checked(offset + bytes.Length);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }

            if (metadata.ContentCrc32C.HasValue && checksum != metadata.ContentCrc32C.Value)
            {
                throw new PantsCorruptionException(
                    $"Cloud SST '{name}' content checksum differs from its manifest.");
            }

            var stagedSource = LocalAsyncSstSource.Open(stagingPath);
            await using var stagedReader = await AsyncSstReader.OpenAsync(
                    stagedSource,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            _lease.EnsureValid();
            AtomicStagedFile.WithPathLock(path, () =>
            {
                if (!File.Exists(path))
                {
                    File.Move(stagingPath, path);
                    AtomicStagedFile.FlushDirectory(_sstDirectory);
                }

                return true;
            });
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    public void EvictLocalSst(string name)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        _ = GetManifestSst(name);
        RemoveSstFromCaches(name);
        File.Delete(Path.Combine(_sstDirectory, name));
    }

    public int CountCompactionInputs(RuntimeState state, bool force)
    {
        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var snapshotHorizon = state.ActiveSnapshots
            .Select(static snapshot => snapshot.BeginSequence)
            .Cast<long?>()
            .Min();
        return _familyIds.Values.Sum(familyId =>
            LeveledCompactionPlanner.Pick(
                manifest.Files,
                familyId,
                _compaction,
                snapshotHorizon,
                force)?.Inputs.Count ?? 0);
    }

    public ValueTask<CompactionResult> CompactAsync(
        RuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool flushMutableOperations,
        Action<long>? publicationCompleted = null,
        ResourceBudget? compactionBudget = null,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs = null,
        CancellationToken cancellationToken = default) =>
        CompactAsync(
            state,
            force,
            outputPublisher,
            flushMutableOperations,
            false,
            publicationCompleted,
            compactionBudget,
            prepareInputs,
            cancellationToken);

    public void Flush(RuntimeState state)
    {
        lock (_walStateGate)
        {
            FlushCore();
        }
    }

    public void Flush(RuntimeState state, ColumnFamilyIdentity identity)
    {
        lock (_walStateGate)
        {
            FlushCore(identity);
        }
    }

    public FlushPublicationPlan BuildFrozenFlushPlan(FrozenMemtableFlush frozen)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        _failpoints.Hit(Failpoint.BeforeFlushBuild);
        _lease.EnsureValid();
        return BuildFlushPlan(
            frozen.Operations,
            frozen.ColumnFamilyId,
            frozen.SstSequence,
            frozen.FrontierSequence,
            $"{_lease.Epoch}.{frozen.Id}");
    }

    public FlushPublicationResult PublishFrozenFlushPlan(
        FrozenMemtableFlush frozen,
        FlushPublicationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        var published = Volatile.Read(ref _manifestReadSnapshot).Files.SingleOrDefault(file =>
            file.Name == frozen.SstName);
        if (published is not null)
        {
            _failpoints.Hit(Failpoint.BeforePublishedFlushRetryValidation);
            ValidatePublishedFlushOutput(frozen, published, plan);
            _lease.EnsureValid();
            CompleteFrozenFlush(frozen);
            RemoveFlushIntents([frozen.SstName]);
            return new FlushPublicationResult(false);
        }

        _failpoints.Hit(Failpoint.BeforeFlushPublication);
        _lease.EnsureValid();
        var persistenceAnomaly = PublishFlushPlan(
            plan,
            frozen.FrontierSequence,
            true);
        CompleteFrozenFlush(frozen);
        return new FlushPublicationResult(persistenceAnomaly);
    }

    public WalCommitResult AppendCommit(
        CommitPayload payload,
        RuntimeState state,
        PantsDurability durability,
        WalMetricsRecorder? metrics = null)
    {
        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            var walLength = _walStream.Length;
            var reservedSequence = checked((long)_nextSequence);
            var unflushedCommitSequence = _unflushedCommitSequence;
            var walRecords = _walRecords;
            var mutableOperationSequence = _mutableOperations.LastSequence;
            var durabilityState = CaptureWalDurabilityState();
            try
            {
                return AppendCommitCore(
                    payload,
                    state,
                    durability,
                    out reservedSequence,
                    metrics);
            }
            catch (Exception appendFailure)
            {
                try
                {
                    _failpoints.Hit(Failpoint.BeforeWalRollback);
                    RollBackWalAppend(
                        state,
                        walLength,
                        reservedSequence,
                        unflushedCommitSequence,
                        walRecords,
                        mutableOperationSequence,
                        durabilityState);
                }
                catch (Exception rollbackFailure)
                {
                    var uncertainty = new WalCommitRollbackException(
                        appendFailure,
                        rollbackFailure);
                    Volatile.Write(ref _walWriteFailure, uncertainty);
                    state.Health = PantsEngineHealth.Degraded;
                    throw uncertainty;
                }

                throw;
            }
        }
    }

    public WalCommitGroupResult AppendCommitGroup(
        IReadOnlyList<WalCommitGroupEntry> commits,
        RuntimeState state,
        PantsDurability durability,
        Action beforeSync,
        WalMetricsRecorder? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(beforeSync);
        if (commits.Count == 0)
        {
            throw new PantsInternalException("A WAL commit group must not be empty.");
        }

        if (durability is not (PantsDurability.Sync or
            PantsDurability.Buffered or
            PantsDurability.BestEffort))
        {
            throw new PantsInternalException(
                "A WAL commit group must use Sync, Buffered, or BestEffort durability.");
        }

        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            _lease.EnsureValid();
            var walLength = _walStream.Length;
            var reservedSequence = commits[^1].ExpectedSequence;
            if (reservedSequence < state.Sequence)
            {
                throw new PantsInternalException(
                    "The coalesced WAL sequence reservation moved backwards.");
            }

            var unflushedCommitSequence = _unflushedCommitSequence;
            var walRecords = _walRecords;
            var mutableOperationSequence = _mutableOperations.LastSequence;
            var durabilityState = CaptureWalDurabilityState();
            try
            {
                var prepared = new List<PreparedWalCommit>(commits.Count);
                foreach (var commit in commits)
                {
                    prepared.Add(durability == PantsDurability.BestEffort
                        ? PrepareBestEffortResidentCommit(commit.Payload, state)
                        : PrepareResidentWalCommit(commit.Payload, state));
                    if (state.Sequence != commit.ExpectedSequence)
                    {
                        throw new PantsInternalException(
                            "The coalesced WAL sequence did not match its preflight plan.");
                    }
                }

                if (durability != PantsDurability.BestEffort)
                {
                    var appendStarted = Stopwatch.GetTimestamp();
                    var payloads = prepared.Select(static commit => commit.Payload).ToArray();
                    WalCodec.AppendFrames(
                        _walStream.SafeFileHandle,
                        walLength,
                        payloads,
                        () => _failpoints.Hit(Failpoint.MidWalAppend));
                    Interlocked.Add(
                        ref _walBytesWrittenTotal,
                        payloads.Sum(static payload => checked((long)payload.Length + 2 * sizeof(uint))));
                    var appendElapsed = Stopwatch.GetElapsedTime(appendStarted);
                    metrics?.RecordAppend(appendElapsed);
                    _walRecords = checked(_walRecords + prepared.Count);
                    RecordWalAppend(state.Sequence, prepared.Count);
                }

                foreach (var commit in prepared)
                {
                    if (durability != PantsDurability.BestEffort)
                    {
                        _failpoints.Hit(Failpoint.AfterWalAppend);
                    }

                    _mutableOperations.AddRange(commit.Mutations);
                    _unflushedCommitSequence = checked((ulong)commit.Sequence);
                }

                if (durability == PantsDurability.Sync)
                {
                    _failpoints.Hit(Failpoint.BeforeWalFlush);
                    _walStream.Flush(false);
                    _failpoints.Hit(Failpoint.AfterWalFlush);
                    beforeSync();
                    _lease.EnsureValid();
                    var syncStarted = Stopwatch.GetTimestamp();
                    _walStream.Flush(true);
                    var syncElapsed = Stopwatch.GetElapsedTime(syncStarted);
                    RecordWalSync(state.Sequence);
                    metrics?.RecordFsync(syncElapsed, state.Sequence);
                }

                return new WalCommitGroupResult(commits.Count);
            }
            catch (Exception groupFailure)
            {
                try
                {
                    RollBackWalCommitGroup(
                        state,
                        walLength,
                        reservedSequence,
                        unflushedCommitSequence,
                        walRecords,
                        mutableOperationSequence,
                        durabilityState);
                }
                catch (Exception rollbackFailure)
                {
                    var uncertainty = new WalCommitGroupRollbackException(
                        groupFailure,
                        rollbackFailure);
                    Volatile.Write(ref _walWriteFailure, uncertainty);
                    state.Health = PantsEngineHealth.Degraded;
                    throw uncertainty;
                }

                throw;
            }
        }
    }

    public TimeSpan FlushDurabilityBoundary(WalMetricsRecorder? metrics = null)
    {
        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            var started = Stopwatch.GetTimestamp();
            _walStream.Flush(true);
            var elapsed = Stopwatch.GetElapsedTime(started);
            RecordWalSync(_walLastAppendedSequence);
            metrics?.RecordFsync(elapsed, _walLastAppendedSequence);
            return elapsed;
        }
    }

    public SealedWalSegment? SealActiveWalForCloud(
        WalMetricsRecorder? metrics = null,
        Action? validateCloudWriteAuthority = null)
    {
        lock (_walStateGate)
        {
            return SealActiveWalCore(
                true,
                metrics,
                validateCloudWriteAuthority);
        }
    }

    public void CompleteCloudWalSeal(SealedWalSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            if (_walStream.Length != 0 ||
                _walLastAppendedSequence > checked((long)segment.MaximumSequence))
            {
                throw new PantsInternalException(
                    "Cloud WAL pending writes cannot be cleared after the active segment changed.");
            }

            _walPendingWrites = 0;
        }
    }

    public ulong? RotateActiveLocalWal(
        WalMetricsRecorder? metrics = null)
    {
        lock (_walStateGate)
        {
            return SealActiveWalCore(
                    false,
                    metrics,
                    null)
                ?.MaximumSequence;
        }
    }

    public IReadOnlyList<SealedWalSegment> GetSealedWalSegmentsForCloudPublication()
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        return EnumerateSealedWalSegmentPaths(_walDirectory)
            .OrderBy(static path =>
                TryParseSealedWalSegmentId(Path.GetFileName(path), out var segmentId)
                    ? segmentId
                    : ulong.MaxValue)
            .ThenBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(static path => ReadSealedWalSegment(path))
            .ToArray();
    }

    public void DeleteCloudDurableWalSegment(SealedWalSegment segment)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        var path = Path.Combine(_walDirectory, segment.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Manifest-published SST files grouped by column-family id, for pinning onto a
    ///     database-version snapshot at snapshot-creation time so a concurrent flush/compaction
    ///     publication cannot change what an already-open snapshot sees.
    /// </summary>
    public IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> GetVisibleFilesSnapshot() =>
        GetManifestFilesSnapshot()
            .GroupBy(static file => file.ColumnFamilyId)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());

    public async ValueTask<SstEntry?> TryReadPointValueAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var keyCopy = key.ToArray();
        var tombstonesSeen = new List<RangeTombstone>();
        SstEntry? best = null;
        foreach (var candidate in candidatesNewestFirst)
        {
            await using var reader = await OpenAsyncSstReaderAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            tombstonesSeen.AddRange(reader.RangeTombstones);
            var decision = reader.GetPointReadDecision(keyCopy);
            if (decision.Rejected || decision.CandidateBlockIndex < 0)
            {
                continue;
            }

            var firstCandidateBlock = decision.CandidateBlockIndex;
            while (firstCandidateBlock > 0 &&
                   reader.GetFirstKey(firstCandidateBlock).AsSpan().SequenceEqual(keyCopy))
            {
                firstCandidateBlock--;
            }

            for (var blockIndex = firstCandidateBlock;
                 blockIndex <= decision.CandidateBlockIndex;
                 blockIndex++)
            {
                var blockContent = await ReadPointBlockAsync(
                        candidate.Name,
                        reader,
                        blockIndex,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in SstCodec.DecodeDataBlock(blockContent))
                {
                    if (!entry.Key.AsSpan().SequenceEqual(keyCopy))
                    {
                        continue;
                    }

                    if (best is null || entry.Sequence > best.Sequence)
                    {
                        best = entry;
                    }
                }
            }
        }

        return best is null || SstRangeTombstoneMask.Covers(tombstonesSeen, keyCopy, best.Sequence)
            ? null
            : best;
    }

    public bool IsSstAvailable(FileMeta file) =>
        File.Exists(Path.Combine(_sstDirectory, file.Name)) || _remoteSstSourceFactory is not null;

    public async ValueTask<ulong?> GetLatestMutationSequenceAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var keyCopy = key.ToArray();
        ulong? latest = null;
        foreach (var candidate in candidatesNewestFirst)
        {
            await using var reader = await OpenAsyncSstReaderAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            foreach (var tombstone in reader.RangeTombstones)
            {
                if (keyCopy.AsSpan().SequenceCompareTo(tombstone.Start) >= 0 &&
                    keyCopy.AsSpan().SequenceCompareTo(tombstone.End) < 0 &&
                    (latest is null || tombstone.Sequence > latest.Value))
                {
                    latest = tombstone.Sequence;
                }
            }
        }

        var entry = await TryReadPointValueAsync(
                candidatesNewestFirst,
                keyCopy,
                cancellationToken)
            .ConfigureAwait(false);
        return entry is not null && (latest is null || entry.Sequence > latest.Value)
            ? entry.Sequence
            : latest;
    }

    public async ValueTask<bool> HasMutationInRangeAsync(
        IReadOnlyList<FileMeta> candidates,
        ReadOnlyMemory<byte> startInclusive,
        ReadOnlyMemory<byte> endExclusive,
        ulong afterSequence,
        ResourceBudget? resourceBudget,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var start = startInclusive.ToArray();
        var end = endExclusive.ToArray();
        foreach (var candidate in candidates)
        {
            if (candidate.LargestSequence is { } largestSequence && largestSequence <= afterSequence)
            {
                continue;
            }

            if (!IsSstAvailable(candidate) || !OverlapsFileRange(candidate, start, end))
            {
                continue;
            }

            await using var reader = await OpenAsyncSstReaderAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (reader.RangeTombstones.Any(tombstone =>
                    tombstone.Sequence > afterSequence &&
                    RangesOverlap(start, end, tombstone.Start, tombstone.End)))
            {
                return true;
            }

            await using var iterator = new AsyncSstBlockIterator(
                reader,
                PantsScanDirection.Forward,
                start,
                end,
                resourceBudget);
            while (await iterator.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (iterator.Current.Sequence > afterSequence)
                {
                    return true;
                }
            }
        }

        return false;
    }

    bool IStorageReadStore.IsWithinFileRange(FileMeta file, ReadOnlySpan<byte> key) =>
        IsWithinFileRange(file, key);

    static ulong GetManifestVisibleSequenceFloor(ManifestState manifest) =>
        Math.Max(
            manifest.LastPersistedSequence,
            manifest.Files
                .Select(static file => file.LargestSequence ?? file.SmallestSequence ?? 0)
                .DefaultIfEmpty()
                .Max());

    public IReadOnlyList<string> GetCompactionInputNames(RuntimeState state, bool force)
    {
        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var snapshotHorizon = state.ActiveSnapshots
            .Select(static snapshot => snapshot.BeginSequence)
            .Cast<long?>()
            .Min();
        return _familyIds.Values
            .Select(familyId => LeveledCompactionPlanner.Pick(
                manifest.Files,
                familyId,
                _compaction,
                snapshotHorizon,
                force))
            .Where(static plan => plan is not null)
            .SelectMany(static plan => plan!.Inputs)
            .Select(static file => file.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public PantsEngineHealth GetHealth(RuntimeState state)
    {
        if (state.Health != PantsEngineHealth.Healthy)
        {
            return state.Health;
        }

        return GetObsoleteFiles().Any(name => !_snapshotPinnedObsoleteFiles.Contains(name))
            ? PantsEngineHealth.Degraded
            : PantsEngineHealth.Healthy;
    }

    public IReadOnlyList<string> GetObsoleteFiles()
    {
        var owned = GetManifestFilesSnapshot()
            .Select(static file => file.Name)
            .ToHashSet(StringComparer.Ordinal);
        return Directory
            .EnumerateFiles(_sstDirectory, "*.sst", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path))
            .Where(name => !owned.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public bool CollectObsoleteFiles(RuntimeState state)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        if (state.ActiveSnapshotCount != 0)
        {
            return false;
        }

        var collected = _snapshotPinnedObsoleteFiles.Count != 0;
        foreach (var name in _snapshotPinnedObsoleteFiles.ToArray())
        {
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
            _snapshotPinnedObsoleteFiles.Remove(name);
        }

        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var droppedFamilies = manifest.ColumnFamilies
            .Where(static family => family.DeletedAt is not null && !family.Reclaimed)
            .ToArray();
        if (droppedFamilies.Length == 0)
        {
            return collected;
        }

        var edits = new List<JsonElement>();
        var obsoleteNames = new List<string>();
        foreach (var family in droppedFamilies)
        {
            var names = manifest.Files
                .Where(file => file.ColumnFamilyId == family.Id)
                .Select(static file => file.Name)
                .ToArray();
            edits.Add(CreateManifestEdit(
                "ReclaimColumnFamily",
                new { id = family.Id, names }));
            obsoleteNames.AddRange(names);
        }

        DurablyApplyManifestBatch(edits);
        SaveManifestCheckpoint();
        foreach (var name in obsoleteNames)
        {
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
        }

        return true;
    }

    public ColumnFamilyMeta GetColumnFamilyMetadata(ColumnFamilyIdentity identity)
    {
        var metadata = GetColumnFamiliesSnapshot().Single(family => family.Id == identity.Id);
        return metadata.Clone();
    }

    public IReadOnlyList<ColumnFamilyMeta> GetColumnFamilyMetadataSnapshot() =>
        GetColumnFamiliesSnapshot();

    public IReadOnlyList<string> GetPointReadSstNames(
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key)
    {
        var keyCopy = key.ToArray();
        return GetManifestFilesSnapshot()
            .Where(file =>
                file.ColumnFamilyId == columnFamily.Id &&
                IsWithinFileRange(file, keyCopy))
            .Select(static file => file.Name)
            .ToArray();
    }

    public IReadOnlyList<string> GetScanSstNames(
        ColumnFamilyIdentity columnFamily,
        ScanBounds bounds) =>
        GetManifestFilesSnapshot()
            .Where(file =>
                file.ColumnFamilyId == columnFamily.Id &&
                file.SmallestKey is not null &&
                file.LargestKey is not null &&
                bounds.Overlaps(
                    GetMetadataKey(file.SmallestKey),
                    GetMetadataKey(file.LargestKey)))
            .Select(static file => file.Name)
            .ToArray();

    public IReadOnlyList<string> GetManifestSstNames() =>
        GetManifestFilesSnapshot().Select(static file => file.Name).ToArray();

    public void HydrateLocalSst(string name, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        var metadata = GetManifestSst(name);
        if (checked((ulong)bytes.Length) != metadata.SizeBytes ||
            (metadata.ContentCrc32C.HasValue &&
             DiskFormat.Crc32C(bytes) != metadata.ContentCrc32C.Value))
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{name}' does not match its manifest metadata.");
        }

        var bytesCopy = bytes.ToArray();
        _ = SstCodec.Decode(bytesCopy);
        var path = Path.Combine(_sstDirectory, name);
        if (File.Exists(path))
        {
            if (!PositionalFile.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                throw new PantsCorruptionException(
                    $"Local immutable SST '{name}' conflicts with its cloud copy.");
            }

            return;
        }

        AtomicStagedFile.Write(path, bytesCopy);
    }

    /// <summary>
    ///     Resolves a key's newest visible SST entry by comparing write sequences across
    ///     <paramref name="candidatesNewestFirst" /> — file publication order alone is insufficient
    ///     because a newer compaction output can contain an older value than an untouched L0 file.
    ///     This is the real value-supplying counterpart to
    ///     <see cref="RecordPointReadCore" />'s telemetry-only bloom/block-read simulation. Safe to
    ///     call concurrently with the actor's mailbox: it only reads the (lock-free,
    ///     snapshot-pinned) reader/block caches. Deliberately records no telemetry of its own — the
    ///     existing exhaustive <see cref="RecordPointReadCore" /> pass remains the sole source of
    ///     read-amplification telemetry/compaction-trigger signal and of <see cref="PantsPointReadTrace" />
    ///     diagnostics, run unconditionally alongside this resolution (see
    ///     <c>TransactionInstance.GetAsync</c>/<c>GetWithDiagnosticsAsync</c>) so those observability
    ///     paths and the read-amplification-triggered compaction trigger are unaffected by whether a
    ///     value happened to already be resolved from disk.
    /// </summary>
    public SstEntry? TryReadPointValue(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        var keyCopy = key.ToArray();
        var tombstonesSeen = new List<RangeTombstone>();
        SstEntry? best = null;
        foreach (var candidate in candidatesNewestFirst)
        {
            var path = Path.Combine(_sstDirectory, candidate.Name);
            using var readerLease = _readerCache.GetOrAdd(candidate.Name, path, out _);
            var reader = readerLease.Reader;
            tombstonesSeen.AddRange(reader.RangeTombstones);
            var decision = reader.GetPointReadDecision(keyCopy);
            if (decision.Rejected || decision.CandidateBlockIndex < 0)
            {
                continue;
            }

            // FindFloorBlock deliberately selects the last block whose first key is <= the
            // target. When one key's descending-sequence versions cross a block boundary that
            // is the oldest duplicate-key block, not the newest one. Walk back through every
            // block beginning with the key and include its predecessor, which may hold the first
            // (newest) versions at its tail.
            var firstCandidateBlock = decision.CandidateBlockIndex;
            while (firstCandidateBlock > 0 &&
                   reader.GetFirstKey(firstCandidateBlock).AsSpan().SequenceEqual(keyCopy))
            {
                firstCandidateBlock--;
            }

            for (var blockIndex = firstCandidateBlock;
                 blockIndex <= decision.CandidateBlockIndex;
                 blockIndex++)
            {
                var blockContent = ReadPointBlock(candidate.Name, reader, blockIndex);
                foreach (var entry in SstCodec.DecodeDataBlock(blockContent))
                {
                    if (!entry.Key.AsSpan().SequenceEqual(keyCopy))
                    {
                        continue;
                    }

                    if (best is null || entry.Sequence > best.Sequence)
                    {
                        best = entry;
                    }
                }
            }
        }

        return best is null || SstRangeTombstoneMask.Covers(tombstonesSeen, keyCopy, best.Sequence)
            ? null
            : best;
    }

    public ulong? GetLatestMutationSequence(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        var keyCopy = key.ToArray();
        ulong? latest = null;
        var available = candidatesNewestFirst.Where(IsSstAvailable).ToArray();
        foreach (var candidate in available)
        {
            var path = Path.Combine(_sstDirectory, candidate.Name);
            using var readerLease = _readerCache.GetOrAdd(candidate.Name, path, out _);
            foreach (var tombstone in readerLease.Reader.RangeTombstones)
            {
                if (keyCopy.AsSpan().SequenceCompareTo(tombstone.Start) >= 0 &&
                    keyCopy.AsSpan().SequenceCompareTo(tombstone.End) < 0 &&
                    (latest is null || tombstone.Sequence > latest.Value))
                {
                    latest = tombstone.Sequence;
                }
            }
        }

        var entry = TryReadPointValue(available, keyCopy);
        return entry is not null && (latest is null || entry.Sequence > latest.Value)
            ? entry.Sequence
            : latest;
    }

    public bool HasMutationInRange(
        IReadOnlyList<FileMeta> candidates,
        ReadOnlySpan<byte> startInclusive,
        ReadOnlySpan<byte> endExclusive,
        ulong afterSequence)
    {
        ThrowIfDisposed();
        var start = startInclusive.ToArray();
        var end = endExclusive.ToArray();
        foreach (var candidate in candidates)
        {
            if (candidate.LargestSequence is { } largestSequence && largestSequence <= afterSequence)
            {
                continue;
            }

            if (!IsSstAvailable(candidate) || !OverlapsFileRange(candidate, start, end))
            {
                continue;
            }

            var path = Path.Combine(_sstDirectory, candidate.Name);
            using var readerLease = _readerCache.GetOrAdd(candidate.Name, path, out _);
            var reader = readerLease.Reader;
            if (reader.RangeTombstones.Any(tombstone =>
                    tombstone.Sequence > afterSequence &&
                    RangesOverlap(start, end, tombstone.Start, tombstone.End)))
            {
                return true;
            }

            using var iterator = SstBlockIterator.Create(
                reader,
                PantsScanDirection.Forward,
                start,
                end);
            while (iterator.MoveNext())
            {
                if (iterator.Current.Sequence > afterSequence)
                {
                    return true;
                }
            }
        }

        return false;
    }

    byte[] ReadPointBlock(string fileName, SstReader reader, int blockIndex)
    {
        var cacheKey = new SstBlockCacheKey(fileName, blockIndex);
        if (_blockCache.TryGet(cacheKey, out var cachedBlock) && cachedBlock is not null)
        {
            return cachedBlock.Content.ToArray();
        }

        var blockContent = reader.ReadDataBlock(blockIndex);
        _ = _blockCache.Add(cacheKey, blockContent);
        return blockContent;
    }

    async ValueTask<byte[]> ReadPointBlockAsync(
        string fileName,
        AsyncSstReader reader,
        int blockIndex,
        CancellationToken cancellationToken)
    {
        var cacheKey = new SstBlockCacheKey(fileName, blockIndex);
        if (_blockCache.TryGet(cacheKey, out var cachedBlock) && cachedBlock is not null)
        {
            return cachedBlock.Content.ToArray();
        }

        var blockContent = await reader.ReadDataBlockAsync(blockIndex, cancellationToken)
            .ConfigureAwait(false);
        // Cache admission follows both the encoded block CRC/decompression check above and the
        // entry-frame decode. A remote block with a self-consistent CRC but malformed entries
        // must not poison subsequent reads through the shared block cache.
        _ = SstCodec.DecodeDataBlock(blockContent);
        _ = _blockCache.Add(cacheKey, blockContent);
        return blockContent;
    }

    /// <summary>
    ///     Leases a reader and opens a bound-clamped <see cref="SstBlockIterator" /> for each
    ///     candidate file, for a scan's k-way merge (see <c>TransactionScanEnumerator</c>). The
    ///     caller owns disposing each returned source once the scan completes.
    /// </summary>
    public IReadOnlyList<SstScanSource> CreateScanSources(
        IReadOnlyList<FileMeta> candidates,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget = null)
    {
        ThrowIfDisposed();
        var sources = new List<SstScanSource>(candidates.Count);
        try
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(_sstDirectory, candidate.Name);
                var lease = _readerCache.GetOrAdd(candidate.Name, path, out _);
                sources.Add(new SstScanSource(
                    lease,
                    SstBlockIterator.Create(
                        lease.Reader,
                        direction,
                        startInclusive,
                        endExclusive,
                        resourceBudget)));
            }

            return sources;
        }
        catch
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }

            throw;
        }
    }

    public async ValueTask<IReadOnlyList<AsyncSstScanSource>> CreateScanSourcesAsync(
        IReadOnlyList<FileMeta> candidates,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var sources = new List<AsyncSstScanSource>(candidates.Count);
        try
        {
            foreach (var candidate in candidates)
            {
                var reader = await OpenAsyncSstReaderAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    sources.Add(new AsyncSstScanSource(
                        candidate,
                        reader,
                        new AsyncSstBlockIterator(
                            reader,
                            direction,
                            startInclusive,
                            endExclusive,
                            resourceBudget),
                        startInclusive,
                        endExclusive));
                }
                catch
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return sources;
        }
        catch
        {
            foreach (var source in sources)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    async ValueTask<AsyncSstReader> OpenAsyncSstReaderAsync(
        FileMeta file,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_sstDirectory, ValidateSstName(file.Name));
        IAsyncSstSource? source = null;
        if (File.Exists(path))
        {
            try
            {
                source = LocalAsyncSstSource.Open(path);
            }
            catch (Exception exception) when (
                _remoteSstSourceFactory is not null &&
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A verified cache eviction can win the race between the existence check and
                // acquiring the delete-share local read lease. Remote immutable authority is
                // still snapshot-visible, so reopen through it below.
            }
        }

        source ??= _remoteSstSourceFactory is null
            ? null
            : await _remoteSstSourceFactory.OpenAsync(file, cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
        {
            throw new PantsRecoveryFailedException(
                $"Manifest-owned SST '{file.Name}' is missing.");
        }

        return await AsyncSstReader.OpenAsync(source, file, cancellationToken)
            .ConfigureAwait(false);
    }

    public bool RecordPointRead(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key) =>
        RecordPointReadCore(
            telemetry,
            columnFamily,
            key,
            null,
            null,
            out _);

    public bool RecordPointRead(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key,
        IReadOnlySet<string>? hydratedFromCloud,
        out PantsPointReadTrace trace)
    {
        var sstTraces = new List<PantsSstReadTrace>();
        var exceedsBudget = RecordPointReadCore(
            telemetry,
            columnFamily,
            key,
            hydratedFromCloud,
            sstTraces,
            out var keyRangeRejects);
        trace = new PantsPointReadTrace(keyRangeRejects, [.. sstTraces]);
        return exceedsBudget;
    }

    public async ValueTask<(bool ExceedsBudget, PantsPointReadTrace Trace)> RecordPointReadAsync(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        var keyCopy = key.ToArray();
        var familyFiles = GetManifestFilesSnapshot()
            .Where(file => file.ColumnFamilyId == columnFamily.Id)
            .ToArray();
        var candidates = familyFiles
            .Where(file => IsWithinFileRange(file, keyCopy))
            .ToArray();
        var bloomChecks = 0;
        var candidateBlocks = 0;
        var amplificationBlocksRead = 0;
        var dataBlocksRead = 0;
        var bloomTruePositives = 0;
        var bloomFalsePositives = 0;
        var bloomTrueNegatives = 0;
        var blockCacheHits = 0;
        var blockCacheMisses = 0;
        var traces = new List<PantsSstReadTrace>();
        foreach (var candidate in candidates)
        {
            var local = IsSstLocal(candidate.Name);
            await using var reader = await OpenAsyncSstReaderAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            var decision = reader.GetPointReadDecision(keyCopy);
            bloomChecks = checked(bloomChecks + decision.BloomChecks);
            candidateBlocks = checked(candidateBlocks + decision.CandidateBlocks);
            bloomTrueNegatives = checked(bloomTrueNegatives + (decision.Rejected ? 1 : 0));
            amplificationBlocksRead = checked(amplificationBlocksRead + 1 + decision.BlocksRead);
            var blockCacheOutcome = PantsCacheReadOutcome.NotChecked;
            var bloomFilterOutcome = decision.Rejected
                ? PantsBloomFilterOutcome.Rejected
                : PantsBloomFilterOutcome.NotChecked;
            var sstDataBlocksRead = 0;
            if (decision.BlocksRead != 0)
            {
                var cacheKey = new SstBlockCacheKey(candidate.Name, decision.CandidateBlockIndex);
                bool containsKey;
                if (_blockCache.TryGet(cacheKey, out var cachedBlock) && cachedBlock is not null)
                {
                    blockCacheHits++;
                    blockCacheOutcome = PantsCacheReadOutcome.Hit;
                    containsKey = cachedBlock.ContainsKey(keyCopy);
                }
                else
                {
                    blockCacheMisses++;
                    blockCacheOutcome = PantsCacheReadOutcome.Miss;
                    var blockContent = await reader.ReadDataBlockAsync(
                            decision.CandidateBlockIndex,
                            cancellationToken)
                        .ConfigureAwait(false);
                    dataBlocksRead++;
                    sstDataBlocksRead = 1;
                    containsKey = SstCodec.DataBlockContainsKey(blockContent, keyCopy);
                    _ = _blockCache.Add(cacheKey, blockContent);
                }

                if (containsKey)
                {
                    bloomTruePositives++;
                    bloomFilterOutcome = PantsBloomFilterOutcome.TruePositive;
                }
                else
                {
                    bloomFalsePositives++;
                    bloomFilterOutcome = PantsBloomFilterOutcome.FalsePositive;
                }
            }

            traces.Add(new PantsSstReadTrace(
                candidate.Name,
                candidate.Level,
                local ? PantsSstReadTier.Local : PantsSstReadTier.HydratedFromCloud,
                bloomFilterOutcome,
                PantsCacheReadOutcome.Miss,
                blockCacheOutcome,
                sstDataBlocksRead));
        }

        var keyRangeRejects = familyFiles.Length - candidates.Length;
        var exceedsBudget = telemetry.RecordSstRead(new SstReadSample
        {
            SstsTouched = candidates.Length,
            L0SstsTouched = candidates.Count(static file => file.Level == 0),
            AmplificationBlocksRead = amplificationBlocksRead,
            DataBlocksRead = dataBlocksRead,
            ReaderCacheMisses = candidates.Length,
            BlockCacheHits = blockCacheHits,
            BlockCacheMisses = blockCacheMisses,
            CandidateBlocks = candidateBlocks,
            KeyRangeRejects = keyRangeRejects,
            BloomChecks = bloomChecks,
            BloomTruePositives = bloomTruePositives,
            BloomFalsePositives = bloomFalsePositives,
            BloomTrueNegatives = bloomTrueNegatives
        });
        return (exceedsBudget, new PantsPointReadTrace(keyRangeRejects, [.. traces]));
    }

    bool RecordPointReadCore(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key,
        IReadOnlySet<string>? hydratedFromCloud,
        List<PantsSstReadTrace>? traces,
        out int keyRangeRejects)
    {
        var keyCopy = key.ToArray();
        var familyFiles = GetManifestFilesSnapshot()
            .Where(file => file.ColumnFamilyId == columnFamily.Id)
            .ToArray();
        var candidates = familyFiles
            .Where(file => IsWithinFileRange(file, keyCopy))
            .ToArray();
        var bloomChecks = 0;
        var candidateBlocks = 0;
        var amplificationBlocksRead = 0;
        var dataBlocksRead = 0;
        var bloomTruePositives = 0;
        var bloomFalsePositives = 0;
        var bloomTrueNegatives = 0;
        var blockCacheHits = 0;
        var blockCacheMisses = 0;
        var readerCacheHits = 0;
        var readerCacheMisses = 0;
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(_sstDirectory, candidate.Name);
            using var readerLease = _readerCache.GetOrAdd(
                candidate.Name,
                path,
                out var readerCacheHit);
            var reader = readerLease.Reader;
            if (readerCacheHit)
            {
                readerCacheHits++;
            }
            else
            {
                readerCacheMisses++;
            }

            var decision = reader.GetPointReadDecision(keyCopy);
            bloomChecks = checked(bloomChecks + decision.BloomChecks);
            candidateBlocks = checked(candidateBlocks + decision.CandidateBlocks);
            bloomTrueNegatives = checked(bloomTrueNegatives + (decision.Rejected ? 1 : 0));
            amplificationBlocksRead = checked(
                amplificationBlocksRead + 1 + decision.BlocksRead);
            var blockCacheOutcome = PantsCacheReadOutcome.NotChecked;
            var bloomFilterOutcome = decision.Rejected
                ? PantsBloomFilterOutcome.Rejected
                : PantsBloomFilterOutcome.NotChecked;
            var sstDataBlocksRead = 0;
            if (decision.BlocksRead != 0)
            {
                var cacheKey = new SstBlockCacheKey(
                    candidate.Name,
                    decision.CandidateBlockIndex);
                bool containsKey;
                if (_blockCache.TryGet(cacheKey, out var cachedBlock) && cachedBlock is not null)
                {
                    blockCacheHits++;
                    blockCacheOutcome = PantsCacheReadOutcome.Hit;
                    containsKey = cachedBlock.ContainsKey(keyCopy);
                }
                else
                {
                    blockCacheMisses++;
                    blockCacheOutcome = PantsCacheReadOutcome.Miss;
                    var blockContent = reader.ReadDataBlock(decision.CandidateBlockIndex);
                    dataBlocksRead = checked(dataBlocksRead + 1);
                    sstDataBlocksRead = 1;
                    containsKey = SstCodec.DataBlockContainsKey(blockContent, keyCopy);
                    _ = _blockCache.Add(cacheKey, blockContent);
                }

                if (containsKey)
                {
                    bloomTruePositives++;
                    bloomFilterOutcome = PantsBloomFilterOutcome.TruePositive;
                }
                else
                {
                    bloomFalsePositives++;
                    bloomFilterOutcome = PantsBloomFilterOutcome.FalsePositive;
                }
            }

            traces?.Add(new PantsSstReadTrace(
                candidate.Name,
                candidate.Level,
                hydratedFromCloud?.Contains(candidate.Name) is true
                    ? PantsSstReadTier.HydratedFromCloud
                    : PantsSstReadTier.Local,
                bloomFilterOutcome,
                readerCacheHit
                    ? PantsCacheReadOutcome.Hit
                    : PantsCacheReadOutcome.Miss,
                blockCacheOutcome,
                sstDataBlocksRead));
        }

        keyRangeRejects = familyFiles.Length - candidates.Length;
        return telemetry.RecordSstRead(new SstReadSample
        {
            SstsTouched = candidates.Length,
            L0SstsTouched = candidates.Count(static file => file.Level == 0),
            AmplificationBlocksRead = amplificationBlocksRead,
            DataBlocksRead = dataBlocksRead,
            ReaderCacheHits = readerCacheHits,
            ReaderCacheMisses = readerCacheMisses,
            BlockCacheHits = blockCacheHits,
            BlockCacheMisses = blockCacheMisses,
            CandidateBlocks = candidateBlocks,
            KeyRangeRejects = keyRangeRejects,
            BloomChecks = bloomChecks,
            BloomTruePositives = bloomTruePositives,
            BloomFalsePositives = bloomFalsePositives,
            BloomTrueNegatives = bloomTrueNegatives
        });
    }

    public IScanReadValidator CreateScanReadValidator(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ScanBounds bounds)
    {
        var readers = new List<SstReader>();
        var blocks = new List<SstScanBlock>();
        try
        {
            foreach (var file in GetManifestFilesSnapshot().Where(file =>
                         file.ColumnFamilyId == columnFamily.Id &&
                         file.SmallestKey is not null &&
                         file.LargestKey is not null &&
                         bounds.Overlaps(GetMetadataKey(file.SmallestKey), GetMetadataKey(file.LargestKey))))
            {
                var reader = SstReader.Open(Path.Combine(_sstDirectory, file.Name));
                readers.Add(reader);
                for (var blockIndex = 0; blockIndex < reader.DataBlockCount; blockIndex++)
                {
                    var firstKey = reader.GetFirstKey(blockIndex);
                    var nextFirstKey = blockIndex + 1 < reader.DataBlockCount
                        ? reader.GetFirstKey(blockIndex + 1)
                        : null;
                    var overlaps =
                        (bounds.EndExclusive is null ||
                         firstKey.AsSpan().SequenceCompareTo(bounds.EndExclusive) < 0) &&
                        (bounds.StartInclusive is null || nextFirstKey is null ||
                         nextFirstKey.AsSpan().SequenceCompareTo(bounds.StartInclusive) > 0);
                    if (overlaps)
                    {
                        blocks.Add(new SstScanBlock(reader, blockIndex, firstKey, nextFirstKey));
                    }
                }
            }

            return new SstScanReadValidator(telemetry, readers, blocks, readers.Count);
        }
        catch
        {
            readers.ForEach(static reader => reader.Dispose());
            throw;
        }
    }

    internal static byte[] GetMetadataKey(IReadOnlyList<int> key) =>
        key.Select(static value => checked((byte)value)).ToArray();

    public static LocalDiskStore Open(
        string directory,
        RuntimeState state,
        ulong minimumWriterEpoch = 0,
        PantsRecoveryPolicy recoveryPolicy = PantsRecoveryPolicy.Strict,
        PantsPerformanceGoal performanceGoal = PantsPerformanceGoal.Latency,
        TimeSpan? leaseClockSkewTolerance = null,
        Action? leaseLossCallback = null,
        IFailpointHandler? failpoints = null,
        PantsCompactionConfiguration? compaction = null,
        long targetSstSizeBytes = 128L * 1024 * 1024,
        PantsBlockCachePolicy blockCachePolicy = PantsBlockCachePolicy.Lru,
        long blockCacheBytes = 0,
        TimeSpan? leaseHeartbeatInterval = null,
        IAsyncSstSourceFactory? remoteSstSourceFactory = null,
        StartupPhaseRecorder? startupPhases = null,
        IPantsClock? leaseClock = null,
        TimeSpan? leaseTimeToLive = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new PantsInvalidArgumentException("DataDirectory must not be empty.");
        }

        var root = Path.GetFullPath(directory);
        FileStream? lockStream = null;
        FileStream? walStream = null;
        FileLease? lease = null;
        var failpointHandler = failpoints ?? NullPantsFailpointHandler.Instance;
        startupPhases ??= new StartupPhaseRecorder(null);
        try
        {
            Directory.CreateDirectory(root);
            try
            {
                lockStream = new FileStream(
                    Path.Combine(root, "LOCK"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                throw new PantsLeaseHeldException(
                    "Another Midge-compatible writer holds the database lock.",
                    exception);
            }

            using (startupPhases.Measure(StartupPhase.Lease))
            {
                lease = FileLease.Acquire(
                    root,
                    minimumWriterEpoch,
                    leaseClockSkewTolerance ?? TimeSpan.FromSeconds(15),
                    leaseLossCallback,
                    leaseHeartbeatInterval ?? TimeSpan.FromSeconds(10),
                    leaseClock,
                    leaseTimeToLive);
            }

            lease.EnsureValid();
            using (startupPhases.Measure(StartupPhase.Format))
            {
                EnsureFormat(root);
            }

            lease.EnsureValid();
            Directory.CreateDirectory(Path.Combine(root, "wal"));
            Directory.CreateDirectory(Path.Combine(root, "sst"));
            Directory.CreateDirectory(Path.Combine(root, "sst", ".flush-staging"));
            var intentPath = Path.Combine(root, "intent_log.json");
            if (!File.Exists(intentPath))
            {
                lease.EnsureValid();
                AtomicStagedFile.Write(intentPath, "[]"u8);
            }

            var journalPath = Path.Combine(root, "manifest.journal");
            if (!File.Exists(journalPath))
            {
                lease.EnsureValid();
                AtomicStagedFile.Write(journalPath, []);
            }

            lease.EnsureValid();
            TransactionSpillStore.CleanupOrphans(root);
            lease.EnsureValid();
            ManifestLoadResult manifestLoad;
            using (startupPhases.Measure(StartupPhase.ManifestSnapshot))
            {
                manifestLoad = LoadManifest(root, recoveryPolicy, state);
            }

            var manifest = manifestLoad.Manifest;
            lease.EnsureValid();
            var recoveryMetadata = ValidateRecoveryMetadata(
                root,
                manifest,
                recoveryPolicy,
                state,
                startupPhases,
                failpointHandler);
            ValidateManifestSstNames(manifest);
            lease.EnsureValid();
            CleanStartupResidue(
                root,
                manifest,
                state,
                manifestLoad.PreserveUnownedSsts || recoveryMetadata.PreserveUnownedSsts,
                failpointHandler,
                lease);
            lease.EnsureValid();
            Directory.CreateDirectory(Path.Combine(root, "sst", ".flush-staging"));
            AdvanceNextWalSequencePastSealedSegments(
                Path.Combine(root, "wal"),
                manifest);
            lease.EnsureValid();
            walStream = new FileStream(Path.Combine(root, "wal", "wal.log"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read);
            var store = new LocalDiskStore(
                root,
                lockStream,
                lease,
                walStream,
                manifest,
                recoveryPolicy,
                performanceGoal,
                failpointHandler,
                compaction ?? new PantsCompactionConfiguration(),
                targetSstSizeBytes,
                blockCachePolicy,
                blockCacheBytes,
                remoteSstSourceFactory);
            lease.EnsureValid();
            store.Recover(state, startupPhases);
            store.RestoreRecoveredWalDurability(state.Sequence);
            lease.EnsureValid();
            store.SaveManifestCheckpoint();
            if (recoveryMetadata.ClearRecoveredIntents)
            {
                lease.EnsureValid();
                store.ClearIntentLog();
            }

            lease.EnsureValid();
            store._walStream.Seek(0, SeekOrigin.End);
            lease.EnsureValid();
            return store;
        }
        catch (PantsException)
        {
            walStream?.Dispose();
            lease?.Dispose();
            lockStream?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            walStream?.Dispose();
            lease?.Dispose();
            lockStream?.Dispose();
            throw new StorageException($"Could not open Pants database at '{root}'.", ex);
        }
    }

    static void CleanStartupResidue(
        string root,
        ManifestState manifest,
        RuntimeState state,
        bool preserveUnownedSsts,
        IFailpointHandler failpoints,
        FileLease lease)
    {
        var sstDirectory = Path.Combine(root, "sst");
        var stagingDirectory = Path.Combine(sstDirectory, ".flush-staging");
        var cloudRecoveryDirectory = Path.Combine(root, "cloud_recovery");
        DeleteStartupResidueDirectory(
            cloudRecoveryDirectory,
            root,
            state,
            StartupCleanupFailureDisposition.WarningOnly,
            failpoints,
            lease);
        DeleteStartupResidueDirectory(
            stagingDirectory,
            sstDirectory,
            state,
            StartupCleanupFailureDisposition.Degrade,
            failpoints,
            lease);
        DeleteStartupResidue(
            Directory.EnumerateFiles(sstDirectory, "*.tmp", SearchOption.TopDirectoryOnly),
            sstDirectory,
            state,
            StartupCleanupFailureDisposition.WarningOnly,
            failpoints,
            lease);
        DeleteStartupResidue(
            Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith(
                    ".midge_leader.",
                    StringComparison.Ordinal)),
            root,
            state,
            StartupCleanupFailureDisposition.WarningOnly,
            failpoints,
            lease);

        if (preserveUnownedSsts)
        {
            return;
        }

        var owned = manifest.Files
            .Select(static file => file.Name)
            .ToHashSet(StringComparer.Ordinal);
        DeleteStartupResidue(
            Directory.EnumerateFiles(sstDirectory, "*.sst", SearchOption.TopDirectoryOnly)
                .Where(path => !owned.Contains(Path.GetFileName(path))),
            sstDirectory,
            state,
            StartupCleanupFailureDisposition.Degrade,
            failpoints,
            lease);
    }

    static void DeleteStartupResidue(
        IEnumerable<string> paths,
        string directory,
        RuntimeState state,
        StartupCleanupFailureDisposition failureDisposition,
        IFailpointHandler failpoints,
        FileLease lease)
    {
        lease.EnsureValid();
        var candidates = Array.Empty<string>();
        try
        {
            candidates = paths.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HandleStartupCleanupFailure(state, failureDisposition);
            return;
        }

        var deleted = false;
        foreach (var path in candidates)
        {
            try
            {
                lease.EnsureValid();
                failpoints.Hit(Failpoint.BeforeStartupResidueDelete);
                lease.EnsureValid();
                File.Delete(path);
                deleted = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                HandleStartupCleanupFailure(state, failureDisposition);
            }
        }

        if (!deleted)
        {
            return;
        }

        try
        {
            lease.EnsureValid();
            AtomicStagedFile.FlushDirectory(directory);
            lease.EnsureValid();
        }
        catch (IOException)
        {
            HandleStartupCleanupFailure(state, failureDisposition);
        }
    }

    static void DeleteStartupResidueDirectory(
        string path,
        string parentDirectory,
        RuntimeState state,
        StartupCleanupFailureDisposition failureDisposition,
        IFailpointHandler failpoints,
        FileLease lease)
    {
        lease.EnsureValid();
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            failpoints.Hit(Failpoint.BeforeStartupResidueDelete);
            lease.EnsureValid();
            Directory.Delete(path, true);
            lease.EnsureValid();
            AtomicStagedFile.FlushDirectory(parentDirectory);
            lease.EnsureValid();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            HandleStartupCleanupFailure(state, failureDisposition);
        }
    }

    static void HandleStartupCleanupFailure(
        RuntimeState state,
        StartupCleanupFailureDisposition failureDisposition)
    {
        if (failureDisposition == StartupCleanupFailureDisposition.WarningOnly)
        {
            return;
        }

        if (state.Health == PantsEngineHealth.Healthy)
        {
            state.Health = PantsEngineHealth.Degraded;
        }
    }

    static void AdvanceNextWalSequencePastSealedSegments(
        string walDirectory,
        ManifestState manifest)
    {
        var maximumSegmentId = EnumerateSealedWalSegmentPaths(walDirectory)
            .Select(static path =>
                TryParseSealedWalSegmentId(Path.GetFileName(path), out var segmentId)
                    ? segmentId
                    : 0)
            .DefaultIfEmpty()
            .Max();
        if (maximumSegmentId == ulong.MaxValue)
        {
            throw new PantsResourceLimitException("The WAL segment sequence is exhausted.");
        }

        manifest.NextWalSeq = Math.Max(
            manifest.NextWalSeq,
            checked(maximumSegmentId + 1));
    }

    public void CreateColumnFamily(ColumnFamilyIdentity identity)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        var edit = CreateManifestEdit(
            "CreateColumnFamily",
            new
            {
                id = identity.Id,
                name = identity.Name,
                created_at = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            });
        DurablyApplyManifestEdit(edit);
        _familyIds[identity] = identity.Id;
        SaveManifestCheckpoint();
    }

    public static JsonElement CreateColumnFamilyEdit(ColumnFamilyIdentity identity) =>
        CreateManifestEdit(
            "CreateColumnFamily",
            new
            {
                id = identity.Id,
                name = identity.Name,
                created_at = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            });

    public JsonElement CreateDropColumnFamilyEdit(
        RuntimeState state,
        ColumnFamilyIdentity identity)
    {
        var snapshot = Volatile.Read(ref _manifestReadSnapshot);
        lock (_manifestGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            _lease.EnsureValid();
            if (!_familyIds.TryGetValue(identity, out var id))
            {
                throw new StorageException(
                    $"Column family '{identity}' has no persistent Midge identity.");
            }

            var droppedSstNames = snapshot.Files
                .Where(file => file.ColumnFamilyId == id)
                .Select(static file => file.Name)
                .ToArray();
            return CreateManifestEdit(
                "DropColumnFamilyAt",
                new
                {
                    id,
                    drop_sequence = checked((ulong)state.Sequence),
                    dropped_sst_names = droppedSstNames
                });
        }
    }

    public bool IsColumnFamilyEditApplied(JsonElement edit)
        => CloudDdlEdit.Matches(
            Volatile.Read(ref _manifestReadSnapshot).ColumnFamilies,
            edit);

    public void CommitColumnFamilyEdit(RuntimeState state, JsonElement edit)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        CloudDdlEdit.Validate(edit);
        if (IsColumnFamilyEditApplied(edit))
        {
            ApplyColumnFamilyEditVisibility(state, edit);
            return;
        }

        _failpoints.Hit(Failpoint.BeforeDdlLocalCommit);
        DurablyApplyManifestEdit(edit);
        _failpoints.Hit(Failpoint.AfterDdlLocalJournalBeforeVisibility);
        ApplyColumnFamilyEditVisibility(state, edit);
        SaveManifestCheckpoint();
    }

    public void AdoptRemoteCommittedColumnFamilyEdit(
        RuntimeState state,
        JsonElement edit)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        CloudDdlEdit.Validate(edit);
        if (!IsColumnFamilyEditApplied(edit))
        {
            DurablyApplyManifestEdit(edit);
        }

        ApplyColumnFamilyEditVisibility(state, edit);
    }

    public void ApplyColumnFamilyEditVisibility(RuntimeState state, JsonElement edit)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        CloudDdlEdit.Validate(edit);
        var id = CloudDdlEdit.GetColumnFamilyId(edit);
        if (CloudDdlEdit.IsCreate(edit))
        {
            var name = CloudDdlEdit.GetColumnFamilyName(edit);
            var existing = state.FamilyData.Keys
                .Where(identity => identity.Id == id)
                .Select(static identity => (ColumnFamilyIdentity?)identity)
                .FirstOrDefault();
            if (existing.HasValue)
            {
                _familyIds[existing.Value] = id;
                state.NextColumnFamilyId = Math.Max(
                    state.NextColumnFamilyId,
                    checked(id + 1));
                return;
            }

            var generation = state.FamilyGeneration.TryGetValue(name, out var currentGeneration)
                ? checked(currentGeneration + 1)
                : 0;
            var identity = new ColumnFamilyIdentity(id, name, generation);
            state.FamilyGeneration[name] = generation;
            state.ActiveFamilyVersions[name] = generation;
            state.FamilyData[identity] = RuntimeState.EmptyFamily;
            state.RangeTombstones[identity] = [];
            state.ActiveMemtableBytes[identity] = 0;
            state.NextColumnFamilyId = Math.Max(state.NextColumnFamilyId, checked(id + 1));
            _familyIds[identity] = id;
            return;
        }

        var dropped = _familyIds
            .Where(pair => pair.Value == id)
            .Select(static pair => (ColumnFamilyIdentity?)pair.Key)
            .FirstOrDefault();
        if (!dropped.HasValue)
        {
            return;
        }

        var droppedIdentity = dropped.Value;
        state.ActiveFamilyVersions.Remove(droppedIdentity.Name);
        state.FamilyData.Remove(droppedIdentity);
        state.RangeTombstones.Remove(droppedIdentity);
        state.ActiveMemtableBytes.Remove(droppedIdentity);
        state.UnflushedFamilies.Remove(droppedIdentity);
        _familyIds.Remove(droppedIdentity);
    }

    public void DropColumnFamily(RuntimeState state, ColumnFamilyIdentity identity)
    {
        lock (_walStateGate)
        {
            lock (_manifestGate)
            {
                ThrowIfDisposed();
                ThrowIfWalWriteFailed();
                _lease.EnsureValid();
                if (!_familyIds.TryGetValue(identity, out var id))
                {
                    throw new StorageException(
                        $"Column family '{identity}' has no persistent Midge identity.");
                }

                var droppedSstNames = _manifest.Files
                    .Where(file => file.ColumnFamilyId == id)
                    .Select(file => file.Name)
                    .ToArray();
                var edit = CreateManifestEdit(
                    "DropColumnFamilyAt",
                    new
                    {
                        id,
                        drop_sequence = checked((ulong)state.Sequence),
                        dropped_sst_names = droppedSstNames
                    });
                DurablyApplyManifestEditCore(edit);
                _ = _mutableOperations.DetachFamily(id);
                _unflushedCommitSequence = _mutableOperations.Count == 0
                    ? _manifest.LastPersistedSequence
                    : checked(_mutableOperations.LastSequence + 1);
                _familyIds.Remove(identity);
                SaveManifestCheckpointCore();
            }
        }
    }

    PreparedWalCommit PrepareResidentWalCommit(
        CommitPayload payload,
        RuntimeState state)
    {
        if (payload.IsSpilled)
        {
            throw new PantsInternalException(
                "A spilled transaction cannot enter resident WAL coalescing.");
        }

        payload.Operations.Validate();
        var (beginSequence, sequence) = ReserveResidentCommitSequence(payload, state);
        _failpoints.Hit(Failpoint.BeforeWalAppend);
        var mutations = ReadMutations(payload, beginSequence);
        _failpoints.Hit(Failpoint.BeforeDirectTransactionCommitMarker);
        var walPayload = WalCodec.EncodeTransactionBatch(
            checked((ulong)payload.TransactionId),
            beginSequence,
            _lease.Epoch,
            mutations);
        return new PreparedWalCommit(sequence, walPayload, mutations);
    }

    PreparedWalCommit PrepareBestEffortResidentCommit(
        CommitPayload payload,
        RuntimeState state)
    {
        if (payload.IsSpilled)
        {
            throw new PantsInternalException(
                "A spilled transaction cannot enter resident commit coalescing.");
        }

        payload.Operations.Validate();
        var (beginSequence, sequence) = ReserveResidentCommitSequence(payload, state);
        return new PreparedWalCommit(sequence, [], ReadMutations(payload, beginSequence));
    }

    (ulong BeginSequence, long CommitSequence) ReserveResidentCommitSequence(
        CommitPayload payload,
        RuntimeState state)
    {
        var beginSequence = checked(_nextSequence + 1);
        if (payload.Operations.Count == 0 ||
            payload.Operations.Count == ulong.MaxValue ||
            beginSequence > ulong.MaxValue - payload.Operations.Count - 1)
        {
            throw new StorageException("The transaction sequence range is exhausted.");
        }

        var commitSequence = beginSequence + payload.Operations.Count + 1;
        var sequence = checked((long)commitSequence);
        _nextSequence = commitSequence;
        state.Sequence = sequence;
        return (beginSequence, sequence);
    }

    WalCommitResult AppendCommitCore(
        CommitPayload payload,
        RuntimeState state,
        PantsDurability durability,
        out long reservedSequence,
        WalMetricsRecorder? metrics,
        bool flushBufferedWrites = true)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        reservedSequence = checked((long)_nextSequence);
        if (payload.Operations.Count == 0)
        {
            return default;
        }

        payload.Operations.Validate();
        var beginSequence = checked(_nextSequence + 1);
        if (payload.Operations.Count == ulong.MaxValue ||
            beginSequence > ulong.MaxValue - payload.Operations.Count - 1)
        {
            throw new StorageException("The transaction sequence range is exhausted.");
        }

        var commitSequence = beginSequence + payload.Operations.Count + 1;
        reservedSequence = checked((long)commitSequence);
        _nextSequence = commitSequence;
        state.Sequence = reservedSequence;
        List<WalMutation>? residentMutations = null;
        var appendElapsed = TimeSpan.Zero;
        if (durability != PantsDurability.BestEffort)
        {
            _failpoints.Hit(Failpoint.BeforeWalAppend);
            var walRecordsBeforeAppend = _walRecords;
            var appendStarted = Stopwatch.GetTimestamp();
            if (payload.IsSpilled)
            {
                AppendSpilledTransaction(
                    payload,
                    checked((ulong)payload.TransactionId),
                    beginSequence,
                    commitSequence);
            }
            else
            {
                residentMutations = ReadMutations(payload, beginSequence);
                AppendDirectTransaction(
                    checked((ulong)payload.TransactionId),
                    beginSequence,
                    residentMutations);
            }

            appendElapsed = Stopwatch.GetElapsedTime(appendStarted);
            metrics?.RecordAppend(appendElapsed);
            RecordWalAppend(
                reservedSequence,
                checked(_walRecords - walRecordsBeforeAppend));
            _failpoints.Hit(Failpoint.AfterWalAppend);
        }

        if (residentMutations is not null)
        {
            _mutableOperations.AddRange(residentMutations);
        }
        else
        {
            payload.Operations.ForEach(operation =>
                _mutableOperations.Add(CreateMutation(
                    operation,
                    checked(beginSequence + operation.Ordinal + 1))));
        }

        _unflushedCommitSequence = commitSequence;
        Exception? postDurabilityFailure = null;
        if (durability != PantsDurability.BestEffort &&
            (durability == PantsDurability.Sync || flushBufferedWrites))
        {
            _failpoints.Hit(Failpoint.BeforeWalFlush);
            var flushStarted = Stopwatch.GetTimestamp();
            _walStream.Flush(durability == PantsDurability.Sync);
            var flushElapsed = Stopwatch.GetElapsedTime(flushStarted);
            if (durability == PantsDurability.Sync)
            {
                RecordWalSync(reservedSequence);
                metrics?.RecordFsync(flushElapsed, reservedSequence);
            }

            try
            {
                _failpoints.Hit(Failpoint.AfterWalFlush);
            }
            catch (Exception exception)
            {
                postDurabilityFailure = exception;
            }
        }

        return new WalCommitResult(postDurabilityFailure);
    }

    void RollBackWalCommitGroup(
        RuntimeState state,
        long walLength,
        long reservedSequence,
        ulong unflushedCommitSequence,
        int walRecords,
        ulong mutableOperationSequence,
        WalDurabilityState durabilityState)
    {
        try
        {
            _failpoints.Hit(Failpoint.BeforeCoalescedWalRollback);
            RollBackWalAppend(
                state,
                walLength,
                reservedSequence,
                unflushedCommitSequence,
                walRecords,
                mutableOperationSequence,
                durabilityState);
        }
        catch
        {
            RestoreWalAppendState(
                state,
                reservedSequence,
                unflushedCommitSequence,
                walRecords,
                mutableOperationSequence,
                durabilityState);
            throw;
        }
    }

    void RollBackWalAppend(
        RuntimeState state,
        long walLength,
        long reservedSequence,
        ulong unflushedCommitSequence,
        int walRecords,
        ulong mutableOperationSequence,
        WalDurabilityState durabilityState)
    {
        try
        {
            _walStream.SetLength(walLength);
            _walStream.Position = walLength;
            _walStream.Flush(true);
        }
        finally
        {
            RestoreWalAppendState(
                state,
                reservedSequence,
                unflushedCommitSequence,
                walRecords,
                mutableOperationSequence,
                durabilityState);
        }
    }

    void RestoreWalAppendState(
        RuntimeState state,
        long reservedSequence,
        ulong unflushedCommitSequence,
        int walRecords,
        ulong mutableOperationSequence,
        WalDurabilityState durabilityState)
    {
        _mutableOperations.TruncateAfter(mutableOperationSequence);

        _nextSequence = checked((ulong)reservedSequence);
        state.Sequence = reservedSequence;
        _unflushedCommitSequence = unflushedCommitSequence;
        _walRecords = walRecords;
        RestoreWalDurabilityState(durabilityState);
    }

    WalDurabilityState CaptureWalDurabilityState() =>
        new(
            _walPendingWrites,
            _walLastAppendedSequence,
            _walLastSyncedSequence,
            _walLocalDurableSequence);

    void RestoreWalDurabilityState(WalDurabilityState state)
    {
        _walPendingWrites = state.PendingWrites;
        _walLastAppendedSequence = state.LastAppendedSequence;
        _walLastSyncedSequence = state.LastSyncedSequence;
        _walLocalDurableSequence = state.LocalDurableSequence;
    }

    void RestoreRecoveredWalDurability(long recoveredSequence)
    {
        lock (_walStateGate)
        {
            _walPendingWrites = 0;
            _walLastAppendedSequence = recoveredSequence;
            _walLastSyncedSequence = 0;
            _walLocalDurableSequence = recoveredSequence;
        }
    }

    void RecordWalAppend(long sequence, int physicalRecordCount)
    {
        _walPendingWrites = checked(_walPendingWrites + physicalRecordCount);
        _walLastAppendedSequence = Math.Max(_walLastAppendedSequence, sequence);
    }

    void RecordWalSync(long sequence)
    {
        _walPendingWrites = 0;
        _walLastSyncedSequence = Math.Max(_walLastSyncedSequence, sequence);
        _walLocalDurableSequence = Math.Max(_walLocalDurableSequence, sequence);
    }

    void AppendDirectTransaction(
        ulong transactionId,
        ulong beginSequence,
        IReadOnlyList<WalMutation> mutations)
    {
        _failpoints.Hit(Failpoint.BeforeDirectTransactionCommitMarker);
        var payload = WalCodec.EncodeTransactionBatch(
            transactionId,
            beginSequence,
            _lease.Epoch,
            mutations);
        var offset = _walStream.Length;
        AppendWalFrame(
            ref offset,
            payload,
            () => _failpoints.Hit(Failpoint.MidWalAppend));
    }

    void AppendSpilledTransaction(
        CommitPayload payload,
        ulong transactionId,
        ulong beginSequence,
        ulong commitSequence)
    {
        var offset = _walStream.Length;
        AppendWalFrame(
            ref offset,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                transactionId,
                beginSequence,
                _lease.Epoch));
        payload.Operations.ForEach(operation =>
        {
            var mutation = CreateMutation(
                operation,
                checked(beginSequence + operation.Ordinal + 1));
            AppendWalFrame(
                ref offset,
                WalCodec.EncodeTransactionMutation(
                    mutation,
                    transactionId,
                    _lease.Epoch));
        });

        _failpoints.Hit(Failpoint.BeforeSpilledTransactionCommitMarker);
        AppendWalFrame(
            ref offset,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                transactionId,
                commitSequence,
                _lease.Epoch));
    }

    List<WalMutation> ReadMutations(
        CommitPayload payload,
        ulong beginSequence)
    {
        if (payload.Operations.Count > int.MaxValue)
        {
            throw new StorageException("The transaction contains too many resident operations.");
        }

        var mutations = new List<WalMutation>((int)payload.Operations.Count);
        payload.Operations.ForEach(operation =>
            mutations.Add(CreateMutation(
                operation,
                checked(beginSequence + operation.Ordinal + 1))));
        return mutations;
    }

    WalMutation CreateMutation(
        TransactionIntentOperation operation,
        ulong sequence) =>
        new(
            ResolveFamilyId(operation.Family),
            operation.Kind switch
            {
                CommitOperationKind.Put when operation.InsertOnly => WalOperation.Insert,
                CommitOperationKind.Put => WalOperation.Put,
                CommitOperationKind.Delete => WalOperation.Delete,
                CommitOperationKind.DeleteRange => WalOperation.DeleteRange,
                _ => throw new StorageException(
                    $"Unsupported WAL operation '{operation.Kind}'.")
            },
            operation.Key.ToArray(),
            operation.Value?.ToArray(),
            sequence,
            operation.ExpirationUnixMilliseconds,
            operation.EndExclusive?.ToArray());

    void AppendWalFrame(ref long offset, byte[] payload, Action? afterPartialPayload = null)
    {
        WalCodec.AppendFrame(
            _walStream.SafeFileHandle,
            offset,
            payload,
            afterPartialPayload);
        var frameBytes = checked((long)payload.Length + 2 * sizeof(uint));
        offset = checked(offset + frameBytes);
        Interlocked.Add(ref _walBytesWrittenTotal, frameBytes);
        _walRecords = checked(_walRecords + 1);
    }

    public FrozenMemtableFlush? FreezeMemtable(
        ColumnFamilyIdentity identity,
        long sizeBytes,
        ulong frontierSequence)
    {
        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            _lease.EnsureValid();
            if (!_familyIds.TryGetValue(identity, out var familyId))
            {
                throw PantsException.Create(
                    PantsErrorCode.InvalidArgument,
                    $"Column family '{identity.Name}' is not active in persistent storage.");
            }

            var operations = _mutableOperations.DetachFamily(familyId);
            if (operations.Count == 0)
            {
                return null;
            }

            var manifestState = GetFlushManifestState(familyId);
            _unflushedCommitSequence = _mutableOperations.Count == 0
                ? manifestState.LastPersistedSequence
                : checked(_mutableOperations.LastSequence + 1);
            var sstSequence = Math.Max(
                manifestState.NextSstSequence,
                _reservedFlushSstSequences.GetValueOrDefault(familyId, 1UL));
            _reservedFlushSstSequences[familyId] = checked(sstSequence + 1);
            var id = checked(++_nextFrozenFlushId);
            _frozenFlushIds.Add(id);
            return new FrozenMemtableFlush(
                id,
                identity,
                familyId,
                operations,
                sstSequence,
                frontierSequence,
                sizeBytes);
        }
    }

    void FlushCore()
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        if (_mutableOperations.Count == 0)
        {
            SaveManifestCheckpoint();
            return;
        }

        FlushOperations(_mutableOperations.SnapshotAll(), _unflushedCommitSequence);
        RotateWal();
        _mutableOperations.Clear();
    }

    void FlushCore(ColumnFamilyIdentity identity)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        if (!_familyIds.TryGetValue(identity, out var familyId))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is not active in persistent storage.");
        }

        var familyOperations = _mutableOperations.DetachFamily(familyId);
        if (familyOperations.Count == 0)
        {
            SaveManifestCheckpoint();
            return;
        }

        if (_mutableOperations.Count == 0)
        {
            FlushOperations(familyOperations, _unflushedCommitSequence);
            RotateWal();
            return;
        }

        FlushOperations(familyOperations, null);
        _unflushedCommitSequence = checked(_mutableOperations.LastSequence + 1);
    }

    void CompleteFrozenFlush(FrozenMemtableFlush frozen)
    {
        lock (_walStateGate)
        {
            ThrowIfDisposed();
            ThrowIfWalWriteFailed();
            if (!_frozenFlushIds.Contains(frozen.Id))
            {
                return;
            }

            _lease.EnsureValid();
            lock (_manifestGate)
            {
                _lease.EnsureValid();
                if (_manifest.LastPersistedSequence < frozen.FrontierSequence)
                {
                    _manifest.LastPersistedSequence = frozen.FrontierSequence;
                    SaveManifestCheckpointCore();
                }
            }

            if (_frozenFlushIds.Count == 1 && _mutableOperations.Count == 0)
            {
                _lease.EnsureValid();
                RotateWal();
            }

            _frozenFlushIds.Remove(frozen.Id);
        }
    }

    void FlushOperations(
        IReadOnlyList<WalMutation> operations,
        ulong? persistedSequence)
    {
        ThrowIfWalWriteFailed();
        var plan = BuildFlushPlan(operations);
        _ = PublishFlushPlan(
            plan,
            persistedSequence,
            false);
    }

    FlushPublicationPlan BuildFlushPlan(
        IReadOnlyList<WalMutation> operations,
        uint? assignedFamilyId = null,
        ulong? assignedSequence = null,
        ulong? flushFrontierSequence = null,
        string? stagingIdentity = null)
    {
        var edits = new List<JsonElement>();
        var intents = new List<JsonElement>();
        var outputs = new List<StagedSstOutput>();
        foreach (var familyGroup in operations.GroupBy(static operation => operation.ColumnFamilyId))
        {
            if (IsColumnFamilyDeleted(familyGroup.Key))
            {
                continue;
            }

            var entries = familyGroup
                .Where(operation => operation.Operation != WalOperation.DeleteRange)
                .Select(operation => new SstEntry(
                    operation.Key,
                    operation.Value,
                    operation.Sequence,
                    operation.Expiration,
                    operation.Operation == WalOperation.Delete))
                .ToList();
            var ranges = familyGroup
                .Where(operation =>
                    operation.Operation == WalOperation.DeleteRange && operation.RangeEnd is not null)
                .Select(operation => new RangeTombstone(operation.Key, operation.RangeEnd!, operation.Sequence))
                .ToList();
            var output = StageSst(
                familyGroup.Key,
                0,
                entries,
                ranges,
                Failpoint.AfterFlushOutputDurable,
                assignedFamilyId == familyGroup.Key ? assignedSequence : null,
                stagingIdentity);
            var metadata = output.Metadata;
            outputs.Add(output);
            edits.Add(CreateManifestEdit(
                "BumpNextSstSeq",
                new
                {
                    cf_id = familyGroup.Key,
                    next_seq = checked(metadata.SstSequence + 1)
                }));
            edits.Add(CreateManifestEdit("AddSst", metadata));
            intents.Add(CreateIntentEntry(
                "FlushPublish",
                new
                {
                    phase = "OutputDurable",
                    cf_id = familyGroup.Key,
                    sequence = flushFrontierSequence ?? metadata.LargestSequence ?? 0,
                    file_meta = CreateIntentFileMetadata(metadata)
                }));
        }

        return new FlushPublicationPlan(edits, intents, outputs);
    }

    bool IsColumnFamilyDeleted(uint familyId)
        => Volatile.Read(ref _manifestReadSnapshot).ColumnFamilies
            .SingleOrDefault(family => family.Id == familyId)
            ?.DeletedAt is not null;

    (ulong LastPersistedSequence, ulong NextSstSequence) GetFlushManifestState(
        uint familyId)
    {
        var snapshot = Volatile.Read(ref _manifestReadSnapshot);
        return (
            snapshot.LastPersistedSequence,
            snapshot.NextSstSequences.GetValueOrDefault(familyId, 1UL));
    }

    FileMeta[] GetManifestFilesSnapshot()
    {
        return Volatile.Read(ref _manifestReadSnapshot).Files
            .Select(static file => file.Clone())
            .ToArray();
    }

    ColumnFamilyMeta[] GetColumnFamiliesSnapshot()
    {
        return Volatile.Read(ref _manifestReadSnapshot).ColumnFamilies
            .Select(static family => family.Clone())
            .ToArray();
    }

    bool PublishFlushPlan(
        FlushPublicationPlan plan,
        ulong? persistedSequence,
        bool tolerateCheckpointFailure)
    {
        lock (_manifestGate)
        {
            _lease.EnsureValid();
            foreach (var output in plan.Outputs)
            {
                FinalizeStagedSst(output);
            }

            _failpoints.Hit(Failpoint.BeforeFlushDirectorySync);
            AtomicStagedFile.FlushDirectory(_sstDirectory);
            _failpoints.Hit(Failpoint.AfterFlushFinalizationBeforeIntent);
            _lease.EnsureValid();
            UpsertFlushIntents(plan);
            _failpoints.Hit(Failpoint.BeforeFlushManifestPublish);
            _lease.EnsureValid();
            DurablyApplyManifestBatch(plan.Edits);
            _failpoints.Hit(Failpoint.AfterFlushManifestPublish);
            if (persistedSequence.HasValue)
            {
                _manifest.LastPersistedSequence = Math.Max(
                    _manifest.LastPersistedSequence,
                    persistedSequence.Value);
            }

            _lease.EnsureValid();
            var outputNames = plan.Outputs.Select(static output => output.Metadata.Name);
            if (!tolerateCheckpointFailure)
            {
                SaveManifestCheckpoint();
                RemoveFlushIntents(outputNames);
                return false;
            }

            RemoveFlushIntents(outputNames);
            try
            {
                SaveManifestCheckpoint();
                return false;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    SealedWalSegment? SealActiveWalCore(
        bool forCloudUpload,
        WalMetricsRecorder? metrics,
        Action? validateCloudWriteAuthority)
    {
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        if (_walStream.Length == 0)
        {
            return null;
        }

        if (_walPendingWrites != 0)
        {
            if (forCloudUpload)
            {
                FlushWalForCloudUpload(metrics);
                _failpoints.Hit(Failpoint.AfterCloudWalSealFlush);
                _lease.EnsureValid();
                validateCloudWriteAuthority?.Invoke();
            }
            else
            {
                SyncWalForLocalRotation(metrics);
            }
        }

        _failpoints.Hit(Failpoint.BeforeWalRotation);
        _walStream.Dispose();
        lock (_manifestGate)
        {
            var segmentId = _manifest.NextWalSeq;
            var fileName = $"{segmentId:00000000000000000000}.wal";
            var sealedPath = Path.Combine(_walDirectory, fileName);
            FileStream? replacementStream = null;
            SealedWalSegment? segment = null;
            try
            {
                _failpoints.Hit(Failpoint.AfterWalRotationStreamDisposed);
                File.Move(_walPath, sealedPath, false);
                segment = ReadSealedWalSegment(sealedPath, forCloudUpload);
                _manifest.NextWalSeq = checked(segmentId + 1);
                SaveManifestCheckpoint();
                replacementStream = new FileStream(
                    _walPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                _walStream = replacementStream;
                _walRecords = 0;
                _failpoints.Hit(Failpoint.AfterWalRotation);
                return segment;
            }
            catch (Exception rotationFailure)
            {
                if (replacementStream is not null)
                {
                    if (forCloudUpload && segment is not null)
                    {
                        throw new WalCloudSealCompletedException(
                            segment,
                            rotationFailure);
                    }

                    throw;
                }

                Exception? rollbackFailure = null;
                if (!File.Exists(_walPath) && File.Exists(sealedPath))
                {
                    try
                    {
                        File.Move(sealedPath, _walPath, false);
                    }
                    catch (IOException exception)
                    {
                        // The sealed segment remains immutable and recoverable. A
                        // fresh active segment is safer than appending to it.
                        rollbackFailure = exception;
                    }
                }

                try
                {
                    _failpoints.Hit(Failpoint.BeforeWalRotationRecoveryStreamReopen);
                    var recoveryStream = new FileStream(
                        _walPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.Read);
                    recoveryStream.Seek(0, SeekOrigin.End);
                    _walStream = recoveryStream;
                }
                catch (Exception reopenFailure)
                {
                    var recoveryFailure = rollbackFailure is null
                        ? reopenFailure
                        : new AggregateException(rollbackFailure, reopenFailure);
                    var uncertainty = new WalRotationRecoveryException(
                        rotationFailure,
                        recoveryFailure);
                    Volatile.Write(ref _walWriteFailure, uncertainty);
                    throw uncertainty;
                }

                throw;
            }
        }
    }

    void FlushWalForCloudUpload(WalMetricsRecorder? metrics)
    {
        _walStream.Flush(false);
        // Pinned Midge records both the writer flush and the CloudAsync actor boundary.
        metrics?.RecordFlush();
        metrics?.RecordFlush();
        RecordWalCloudFlush(_walLastAppendedSequence);
    }

    void RecordWalCloudFlush(long sequence)
    {
        _walLastSyncedSequence = Math.Max(_walLastSyncedSequence, sequence);
        _walLocalDurableSequence = Math.Max(_walLocalDurableSequence, sequence);
    }

    void SyncWalForLocalRotation(WalMetricsRecorder? metrics)
    {
        var started = Stopwatch.GetTimestamp();
        _walStream.Flush(true);
        var elapsed = Stopwatch.GetElapsedTime(started);
        RecordWalSync(_walLastAppendedSequence);
        metrics?.RecordFsync(elapsed, _walLastAppendedSequence);
    }

    static IEnumerable<string> EnumerateSealedWalSegmentPaths(string walDirectory) =>
        Directory.EnumerateFiles(walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
            .Concat(Directory
                .EnumerateFiles(walDirectory, "wal_*.log", SearchOption.TopDirectoryOnly)
                .Where(static path =>
                    LegacyWalSegmentFileNameRegex.IsMatch(Path.GetFileName(path))));

    static bool TryParseSealedWalSegmentId(string fileName, out ulong segmentId)
    {
        if (ulong.TryParse(Path.GetFileNameWithoutExtension(fileName), out segmentId))
        {
            return true;
        }

        var match = LegacyWalSegmentFileNameRegex.Match(fileName);
        if (match.Success && ulong.TryParse(match.Groups[1].Value, out segmentId))
        {
            return true;
        }

        segmentId = 0;
        return false;
    }

    static SealedWalSegment ReadSealedWalSegment(
        string path,
        bool requireSingleWriterEpoch = true)
    {
        var fileName = Path.GetFileName(path);
        if (!TryParseSealedWalSegmentId(fileName, out var segmentId))
        {
            throw new PantsCorruptionException($"Sealed WAL name '{fileName}' is invalid.");
        }

        var bytes = File.ReadAllBytes(path);
        ulong maximumSequence = 0;
        ulong? writerEpoch = null;
        try
        {
            WalFrameReader.Visit(
                bytes,
                (record, _) =>
                {
                    if (record.Operation == WalOperation.TransactionBatch)
                    {
                        WalCodec.ValidateTransactionBatch(record);
                    }

                    if (requireSingleWriterEpoch &&
                        writerEpoch.HasValue &&
                        writerEpoch.Value != record.WriterEpoch)
                    {
                        throw new StorageException(
                            $"Sealed WAL '{fileName}' contains mixed writer epochs.");
                    }

                    writerEpoch = record.WriterEpoch;
                    maximumSequence = Math.Max(maximumSequence, record.Sequence);
                });
        }
        catch (PantsException exception) when (exception is not PantsCorruptionException)
        {
            throw new PantsCorruptionException(
                $"Sealed WAL '{fileName}' is malformed.",
                exception);
        }

        if (maximumSequence == 0)
        {
            throw new PantsCorruptionException($"Sealed WAL '{fileName}' contains no transactions.");
        }

        return new SealedWalSegment(
            segmentId,
            writerEpoch!.Value,
            maximumSequence,
            fileName,
            bytes);
    }

    public PantsStorageLayout GetStorageLayout(RuntimeState state)
    {
        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var levels = manifest.Files
            .GroupBy(static file => file.Level)
            .OrderBy(static group => group.Key)
            .Select(group =>
            {
                var files = group
                    .OrderBy(static file => file.Name, StringComparer.Ordinal)
                    .Select(static file => new PantsStorageFileLayout(
                        file.Name,
                        checked((int)file.Level),
                        file.ColumnFamilyId,
                        checked((long)file.SizeBytes),
                        file.SmallestKey is null
                            ? null
                            : new ReadOnlyMemory<byte>(file.SmallestKey.Select(static value => checked((byte)value))
                                .ToArray()),
                        file.LargestKey is null
                            ? null
                            : new ReadOnlyMemory<byte>(file.LargestKey.Select(static value => checked((byte)value))
                                .ToArray()),
                        file.SmallestSequence is null ? null : checked((long)file.SmallestSequence.Value),
                        file.LargestSequence is null ? null : checked((long)file.LargestSequence.Value)))
                    .ToArray();
                return new PantsStorageLevelLayout(
                    checked((int)group.Key),
                    files.Length,
                    files.Sum(static file => file.SizeBytes),
                    files);
            })
            .ToArray();
        var now = state.Clock.UtcNow;
        var snapshots = state.ActiveSnapshots
            .Select(snapshot => new PantsSnapshotPin(
                snapshot.SnapshotId,
                snapshot.BeginSequence,
                now <= snapshot.StartedAtUtc ? TimeSpan.Zero : now - snapshot.StartedAtUtc,
                1))
            .ToArray();
        return new PantsStorageLayout(
            GetHealth(state),
            checked((long)manifest.LastPersistedSequence),
            checked((long)manifest.NextWalSequence),
            levels,
            snapshots,
            0,
            [],
            GetObsoleteFiles());
    }

    public long Compact(
        RuntimeState state,
        bool force,
        bool continueCompacting = false) =>
        CompactAsync(
                state,
                force,
                null,
                true,
                continueCompacting,
                null,
                null,
                null,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            .BytesRewritten;

    public ValueTask<CompactionResult> CompactAsync(
        RuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        CancellationToken cancellationToken = default) =>
        CompactAsync(
            state,
            force,
            outputPublisher,
            true,
            false,
            null,
            null,
            null,
            cancellationToken);

    async ValueTask<CompactionResult> CompactAsync(
        RuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool flushMutableOperations,
        bool continueCompacting,
        Action<long>? publicationCompleted,
        ResourceBudget? compactionBudget,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfWalWriteFailed();
        _lease.EnsureValid();
        if (HasManifestPublishedCompactionIntent())
        {
            throw new PantsBusyException(
                "Compaction is fenced until manifest publication recovery completes.");
        }

        if (flushMutableOperations)
        {
            Flush(state);
        }

        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var obsoleteNames = new List<string>();
        var edits = new List<JsonElement>();
        var intents = new List<JsonElement>();
        var outputNames = new List<string>();
        var outputBytes = 0L;
        var hasCompactionPlan = false;
        try
        {
            foreach (var (_, familyId) in _familyIds.ToList())
            {
                var plan = LeveledCompactionPlanner.Pick(
                    manifest.Files,
                    familyId,
                    _compaction,
                    state.ActiveSnapshots.Select(static snapshot => snapshot.BeginSequence).Cast<long?>().Min(),
                    force);
                if (plan is null)
                {
                    continue;
                }

                hasCompactionPlan = true;
                if (prepareInputs is not null)
                {
                    await prepareInputs(
                            plan.Inputs
                                .Select(static input => input.Name)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // StreamingCompactionMerger opens each input via SstReader (bounded: footer/meta/
                // index/bloom + the small resident range-tombstone list) and walks its entries one
                // block at a time through the k-way merge — the same version-retention/tombstone-
                // masking/GC-eligibility rules as CompactionMerger.Merge +
                // CompactionOutputPartitioner.Partition (see its doc comment), just driven
                // incrementally instead of over one materialized array per input plus one
                // materialized merged/partitioned result.
                var inputReaders = plan.Inputs
                    .Select(input => SstReader.Open(Path.Combine(_sstDirectory, input.Name)))
                    .ToArray();
                var outputs = new List<FileMeta>();
                var firstOutputSequence = manifest.NextSstSequences.TryGetValue(
                    familyId,
                    out var nextOutputSequence)
                    ? nextOutputSequence
                    : 1UL;
                try
                {
                    var outputIndex = 0UL;
                    foreach (var partition in StreamingCompactionMerger.MergeAndPartition(
                                 inputReaders,
                                 plan,
                                 _targetSstSizeBytes,
                                 compactionBudget))
                    {
                        var outputSequence = checked(firstOutputSequence + outputIndex);
                        outputNames.Add(CreateSstFileName(
                            familyId,
                            plan.TargetLevel,
                            outputSequence));
                        var output = CreateSst(
                            familyId,
                            plan.TargetLevel,
                            partition.Entries,
                            partition.RangeTombstones,
                            Failpoint.AfterCompactionOutputDurable,
                            outputSequence);
                        outputs.Add(output);
                        edits.Add(CreateManifestEdit("AddSst", output));
                        outputIndex = checked(outputIndex + 1);
                    }
                }
                finally
                {
                    foreach (var inputReader in inputReaders)
                    {
                        inputReader.Dispose();
                    }
                }

                if (outputs.Count > 0)
                {
                    edits.Add(CreateManifestEdit(
                        "BumpNextSstSeq",
                        new
                        {
                            cf_id = familyId,
                            next_seq = checked(outputs[^1].SstSequence + 1)
                        }));
                }

                edits.AddRange(plan.Inputs.Select(static input => CreateManifestEdit(
                    "RemoveSst",
                    new { name = input.Name })));
                intents.Add(CreateIntentEntry(
                    "CompactionPublish",
                    new
                    {
                        phase = "OutputDurable",
                        cf_id = familyId,
                        removed = plan.Inputs.Select(static input => input.Name).ToArray(),
                        added = outputs.Select(CreateIntentFileMetadata).ToArray()
                    }));

                obsoleteNames.AddRange(plan.Inputs.Select(static input => input.Name));
                outputBytes = checked(outputBytes + outputs.Sum(static output => checked((long)output.SizeBytes)));
            }
        }
        catch
        {
            DeleteUnpublishedCompactionOutputs(outputNames);
            throw;
        }

        foreach (var family in manifest.ColumnFamilies.Where(family =>
                     family.DeletedAt is not null && !family.Reclaimed))
        {
            var droppedFiles = manifest.Files
                .Where(file => file.ColumnFamilyId == family.Id)
                .ToList();
            obsoleteNames.AddRange(droppedFiles.Select(file => file.Name));
            edits.Add(CreateManifestEdit(
                "ReclaimColumnFamily",
                new
                {
                    id = family.Id,
                    names = droppedFiles.Select(static file => file.Name).ToArray()
                }));
        }

        if (edits.Count == 0)
        {
            return new CompactionResult(0, 0, false);
        }

        _lease.EnsureValid();
        if (outputNames.Count > 0)
        {
            try
            {
                _failpoints.Hit(Failpoint.BeforeCompactionDirectorySync);
                AtomicStagedFile.FlushDirectory(_sstDirectory);
                _lease.EnsureValid();
            }
            catch
            {
                DeleteUnpublishedCompactionOutputs(outputNames);
                throw;
            }
        }

        var supersededOutputs = UpsertCompactionIntents(intents);
        DeleteSupersededCompactionOutputs(supersededOutputs);
        if (outputPublisher is not null && outputNames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await outputPublisher(outputNames, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lease.EnsureValid();
        }

        var persistenceAnomaly = false;
        lock (_manifestGate)
        {
            _failpoints.Hit(Failpoint.BeforeCompactionManifestPublish);
            _lease.EnsureValid();
            DurablyApplyManifestBatch(edits);
            if (hasCompactionPlan)
            {
                publicationCompleted?.Invoke(outputBytes);
            }

            _failpoints.Hit(Failpoint.AfterCompactionManifestPublish);
            _lease.EnsureValid();
            TransitionCompactionIntents(intents, "ManifestPublished");
            try
            {
                SaveManifestCheckpoint();
            }
            catch (Exception exception) when (
                outputPublisher is null &&
                exception is IOException or UnauthorizedAccessException)
            {
                persistenceAnomaly = true;
            }

            if (!persistenceAnomaly)
            {
                _lease.EnsureValid();
                RemoveCompactionIntents(intents);
            }
        }

        foreach (var name in obsoleteNames)
        {
            _lease.EnsureValid();
            // The actor's current DatabaseVersion can still name these inputs until it publishes
            // the manifest's new version. Queue every retired file; PublishSnapshot performs the
            // first safe collection while direct-read admission is excluded.
            _snapshotPinnedObsoleteFiles.Add(name);
        }

        _failpoints.Hit(Failpoint.AfterCompactionObsoleteFilesRetired);

        var publicationCount = hasCompactionPlan ? 1 : 0;
        if (!persistenceAnomaly && (force || continueCompacting) && hasCompactionPlan)
        {
            var continued = await CompactAsync(
                state,
                false,
                outputPublisher,
                flushMutableOperations,
                true,
                publicationCompleted,
                compactionBudget,
                prepareInputs,
                cancellationToken).ConfigureAwait(false);
            outputBytes = checked(outputBytes + continued.BytesRewritten);
            publicationCount = checked(publicationCount + continued.PublicationCount);
            persistenceAnomaly |= continued.PersistenceAnomaly;
        }

        return new CompactionResult(outputBytes, publicationCount, persistenceAnomaly);
    }

    void RemoveSstFromCaches(string name)
    {
        _readerCache.RemoveFile(name);
        _blockCache.RemoveFile(name);
    }

    static string CreateSstFileName(uint familyId, uint level, ulong sequence) =>
        $"{familyId:000000}_{level:00}_{sequence:00000000000000000000}.sst";

    void DeleteUnpublishedCompactionOutputs(IEnumerable<string> names)
    {
        var distinctNames = names.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctNames.Length == 0)
        {
            return;
        }

        var stagingDirectory = Path.Combine(_sstDirectory, ".flush-staging");
        foreach (var name in distinctNames)
        {
            File.Delete(Path.Combine(
                stagingDirectory,
                $"{_lease.Epoch}.compaction.{name}.tmp"));
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
        }

        AtomicStagedFile.FlushDirectory(stagingDirectory);
        AtomicStagedFile.FlushDirectory(_sstDirectory);
    }

    FileMeta GetManifestSst(string name)
    {
        var safeName = ValidateSstName(name);
        return Volatile.Read(ref _manifestReadSnapshot).Files.SingleOrDefault(file =>
                   StringComparer.Ordinal.Equals(file.Name, safeName)) ??
               throw new PantsCorruptionException(
                   $"SST '{safeName}' is not owned by the active manifest.");
    }

    void Recover(RuntimeState state, StartupPhaseRecorder startupPhases)
    {
        RestoreColumnFamilies(state);
        using (startupPhases.Measure(StartupPhase.SstHydration))
        {
            foreach (var file in _manifest.Files.ToArray())
            {
                try
                {
                    var name = ValidateSstName(file.Name);
                    var path = Path.Combine(_sstDirectory, name);
                    if (File.Exists(path))
                    {
                        using var reader = SstReader.Open(path);
                        continue;
                    }

                    if (_remoteSstSourceFactory is null)
                    {
                        throw new StorageException($"Manifest SST '{file.Name}' is missing.");
                    }

                    var remoteReader = OpenAsyncSstReaderAsync(file, CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                    remoteReader.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) when (
                    _recoveryPolicy == PantsRecoveryPolicy.Salvage &&
                    exception is PantsException or IOException)
                {
                    state.MarkSalvageMode();
                    _manifest.Files.Remove(file);
                }
                catch (Exception exception) when (exception is PantsException or IOException)
                {
                    throw PantsException.Create(
                        PantsErrorCode.RecoveryFailed,
                        $"Manifest SST '{file.Name}' could not be recovered strictly.",
                        exception);
                }
            }
        }

        using (startupPhases.Measure(StartupPhase.WalReplay))
        {
            ReplayWal(state);
        }

        state.Sequence = checked((long)_nextSequence);
    }

    void ReplayWal(RuntimeState state)
    {
        var persistedFamilySequences = _manifest.Files
            .Where(static file => file.LargestSequence.HasValue)
            .GroupBy(static file => file.ColumnFamilyId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(file => file.LargestSequence!.Value));
        var activeFamilyIds = _familyIds.Values.ToHashSet();
        var sealedSegments = EnumerateSealedWalSegmentPaths(_walDirectory)
            .Where(static path => Path.GetFileName(path) != "wal.log")
            .OrderBy(static path =>
                TryParseSealedWalSegmentId(Path.GetFileName(path), out var segmentId)
                    ? segmentId
                    : ulong.MaxValue)
            .ThenBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        var writerEpochFrontiers = DiscoverWriterEpochFrontiers(state, sealedSegments);
        using var recovery = new WalRecoveryStateMachine();
        var recoveredVersions = new WalRecoveredVersionTracker();
        var replayOrdinal = 0UL;
        for (var index = 0; index < sealedSegments.Length; index++)
        {
            var sealedSegment = sealedSegments[index];
            WalReplayOutcome outcome;
            using (var stream = new FileStream(
                       sealedSegment,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                outcome = ReplayWalStream(
                    state,
                    stream,
                    false,
                    recovery,
                    recoveredVersions,
                    writerEpochFrontiers,
                    ref replayOrdinal,
                    persistedFamilySequences,
                    activeFamilyIds);
            }

            if (outcome == WalReplayOutcome.Salvaged)
            {
                RetainCorruptFile(sealedSegment);
                for (var laterIndex = index + 1; laterIndex < sealedSegments.Length; laterIndex++)
                {
                    RetainCorruptFile(sealedSegments[laterIndex]);
                }

                ResetActiveWalAfterSalvage();
                return;
            }
        }

        _walStream.Seek(0, SeekOrigin.Begin);
        if (ReplayWalStream(
                state,
                _walStream,
                true,
                recovery,
                recoveredVersions,
                writerEpochFrontiers,
                ref replayOrdinal,
                persistedFamilySequences,
                activeFamilyIds) == WalReplayOutcome.Salvaged)
        {
            ResetActiveWalAfterSalvage();
        }
    }

    WalWriterEpochFrontiers DiscoverWriterEpochFrontiers(
        RuntimeState state,
        IReadOnlyList<string> sealedSegments)
    {
        var frontiers = new WalWriterEpochFrontiers();
        var ordinal = 0UL;
        foreach (var sealedSegment in sealedSegments)
        {
            using var stream = new FileStream(
                sealedSegment,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (VisitWalStream(
                    state,
                    stream,
                    false,
                    ref ordinal,
                    (record, _, recordOrdinal) =>
                    {
                        frontiers.Record(record, recordOrdinal);
                        return true;
                    }) == WalReplayOutcome.Salvaged)
            {
                return frontiers;
            }
        }

        _walStream.Seek(0, SeekOrigin.Begin);
        _ = VisitWalStream(
            state,
            _walStream,
            true,
            ref ordinal,
            (record, _, recordOrdinal) =>
            {
                frontiers.Record(record, recordOrdinal);
                return true;
            });
        return frontiers;
    }

    WalReplayOutcome ReplayWalStream(
        RuntimeState state,
        FileStream stream,
        bool allowIncompleteTail,
        WalRecoveryStateMachine recovery,
        WalRecoveredVersionTracker recoveredVersions,
        WalWriterEpochFrontiers writerEpochFrontiers,
        ref ulong replayOrdinal,
        IReadOnlyDictionary<uint, ulong> persistedFamilySequences,
        HashSet<uint> activeFamilyIds)
        => VisitWalStream(
            state,
            stream,
            allowIncompleteTail,
            ref replayOrdinal,
            (record, _, recordOrdinal) =>
            {
                try
                {
                    if (writerEpochFrontiers.IsStale(record, recordOrdinal))
                    {
                        recovery.Accept(record, static (_, _) => { });
                        return true;
                    }

                    state.RecordWalRecovery(WalRecordMetrics.GetLogicalByteCount(record));
                    _nextSequence = Math.Max(_nextSequence, record.Sequence);
                    var recoveredMutations = new List<(WalMutation Mutation, ulong CommitSequence)>();
                    recovery.Accept(
                        record,
                        (mutation, commitSequence) =>
                            recoveredMutations.Add((mutation, commitSequence)));
                    recoveredVersions.ValidateAndRecord(
                        recoveredMutations.Select(static item => item.Mutation).ToArray());
                    var applicableMutations = recoveredMutations
                        .Where(item =>
                        {
                            var mutation = item.Mutation;
                            return activeFamilyIds.Contains(mutation.ColumnFamilyId) &&
                                   mutation.Sequence > persistedFamilySequences.GetValueOrDefault(
                                       mutation.ColumnFamilyId);
                        })
                        .ToArray();
                    foreach (var (mutation, commitSequence) in applicableMutations)
                    {
                        ApplyMutations(state, [mutation]);
                        RecordRecoveredMemtableBytes(state, [mutation]);
                        _mutableOperations.Add(mutation);
                        _unflushedCommitSequence = Math.Max(
                            _unflushedCommitSequence,
                            commitSequence);
                    }
                }
                catch (PantsException exception)
                {
                    return HandleWalCorruption(
                        state,
                        "WAL transaction state is corrupt.",
                        exception) != WalReplayOutcome.Salvaged;
                }

                _walRecords++;
                return true;
            });

    WalReplayOutcome VisitWalStream(
        RuntimeState state,
        FileStream stream,
        bool allowIncompleteTail,
        ref ulong recordOrdinal,
        Func<WalRecord, int, ulong, bool> visitor)
    {
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            var recordStart = stream.Position;
            if (!DiskFormat.ReadExactly(stream, header))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            if (header.IndexOfAnyExcept((byte)0) < 0 && WalFrameReader.IsZeroFilledTail(stream))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length > DiskFormat.WalMaximumRecordBytes)
            {
                return HandleWalCorruption(
                    state,
                    "WAL record exceeds Midge's 64 MiB frame limit.");
            }

            if (length > stream.Length - stream.Position)
            {
                if (allowIncompleteTail &&
                    WalFrameReader.ContainsVerifiedFrameInRemainingBytes(stream))
                {
                    return HandleWalCorruption(
                        state,
                        "A WAL frame length hides a verified later frame.");
                }

                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            var payload = new byte[length];
            if (!DiskFormat.ReadExactly(stream, payload))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            if (DiskFormat.Crc32C(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[4..]))
            {
                return HandleWalCorruption(state, "WAL frame CRC32C mismatch.");
            }

            WalRecord record;
            try
            {
                record = WalCodec.DecodeRecord(payload);
            }
            catch (PantsException exception)
            {
                return HandleWalCorruption(state, "WAL record is corrupt.", exception);
            }

            var currentOrdinal = recordOrdinal;
            if (recordOrdinal != ulong.MaxValue)
            {
                recordOrdinal++;
            }

            if (!visitor(record, payload.Length, currentOrdinal))
            {
                return WalReplayOutcome.Salvaged;
            }
        }

        return WalReplayOutcome.Complete;
    }

    void RecordRecoveredMemtableBytes(
        RuntimeState state,
        IReadOnlyList<WalMutation> mutations)
    {
        var identities = _familyIds.ToDictionary(
            static pair => pair.Value,
            static pair => pair.Key);
        foreach (var operations in mutations.GroupBy(static mutation => mutation.ColumnFamilyId))
        {
            if (!identities.TryGetValue(operations.Key, out var identity))
            {
                continue;
            }

            state.ActiveMemtableBytes[identity] = checked(
                state.ActiveMemtableBytes.GetValueOrDefault(identity) +
                operations.Sum(static operation =>
                    (long)operation.Key.Length +
                    (operation.Value?.Length ?? 0) +
                    (operation.RangeEnd?.Length ?? 0) +
                    64));
            state.UnflushedFamilies.Add(identity);
        }
    }

    WalReplayOutcome HandleIncompleteWalTail(
        RuntimeState state,
        FileStream stream,
        long validLength,
        bool allowIncompleteTail)
    {
        if (!allowIncompleteTail)
        {
            return HandleWalCorruption(
                state,
                "A sealed WAL segment has an incomplete final frame.");
        }

        stream.SetLength(validLength);
        return WalReplayOutcome.ToleratedIncompleteTail;
    }

    WalReplayOutcome HandleWalCorruption(
        RuntimeState state,
        string message,
        Exception? innerException = null)
    {
        if (_recoveryPolicy == PantsRecoveryPolicy.Salvage)
        {
            state.MarkSalvageMode();
            return WalReplayOutcome.Salvaged;
        }

        throw innerException is null
            ? PantsException.Create(PantsErrorCode.RecoveryFailed, message)
            : PantsException.Create(PantsErrorCode.RecoveryFailed, message, innerException);
    }

    void ResetActiveWalAfterSalvage()
    {
        _walStream.Dispose();
        RetainCorruptFile(_walPath);
        _walStream = new FileStream(
            _walPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);
        _walRecords = 0;
    }

    void RestoreColumnFamilies(RuntimeState state)
    {
        state.FamilyGeneration.Clear();
        state.ActiveFamilyVersions.Clear();
        state.FamilyData.Clear();
        state.RangeTombstones.Clear();
        state.ActiveMemtableBytes.Clear();
        if (!_manifest.ColumnFamilies.Any(family => family.Id == 0 && family.Name == "default"))
        {
            var defaultIdentity = new ColumnFamilyIdentity(0, "default", RuntimeState.DefaultFamilyVersion);
            state.FamilyGeneration["default"] = RuntimeState.DefaultFamilyVersion;
            state.ActiveFamilyVersions["default"] = RuntimeState.DefaultFamilyVersion;
            state.FamilyData[defaultIdentity] = RuntimeState.EmptyFamily;
            state.RangeTombstones[defaultIdentity] = [];
            state.ActiveMemtableBytes[defaultIdentity] = 0;
            _familyIds[defaultIdentity] = 0;
        }

        foreach (var nameGroup in _manifest.ColumnFamilies.GroupBy(family => family.Name, StringComparer.Ordinal))
        {
            var ordered = nameGroup.OrderBy(family => family.Id).ToList();
            state.FamilyGeneration[nameGroup.Key] = ordered.Count - 1;
            for (var version = 0; version < ordered.Count; version++)
            {
                var family = ordered[version];
                if (family.DeletedAt is not null)
                {
                    continue;
                }

                var identity = new ColumnFamilyIdentity(family.Id, family.Name, version);
                state.ActiveFamilyVersions[family.Name] = version;
                state.FamilyData[identity] = RuntimeState.EmptyFamily;
                state.RangeTombstones[identity] = [];
                state.ActiveMemtableBytes[identity] = 0;
                _familyIds[identity] = family.Id;
            }
        }

        if (!state.ActiveFamilyVersions.ContainsKey("default"))
        {
            throw new StorageException("Midge manifest does not contain the active default column family.");
        }

        state.NextColumnFamilyId = _manifest.ColumnFamilies.Count == 0
            ? 1
            : checked(_manifest.ColumnFamilies.Max(family => family.Id) + 1);
    }

    void ApplyMutations(RuntimeState state, IEnumerable<WalMutation> mutations)
    {
        var identityById = _familyIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        foreach (var mutation in mutations)
        {
            if (!identityById.TryGetValue(mutation.ColumnFamilyId, out var identity) ||
                !state.FamilyData.TryGetValue(identity, out var family))
            {
                continue;
            }

            switch (mutation.Operation)
            {
                case WalOperation.Put:
                case WalOperation.Insert:
                    family = family.SetItem(mutation.Key, CellState.FromUnixMilliseconds(
                        mutation.Value?.ToArray(),
                        checked((long)mutation.Sequence),
                        mutation.Expiration));
                    break;
                case WalOperation.Delete:
                    family = family.Remove(mutation.Key);
                    break;
                case WalOperation.DeleteRange when mutation.RangeEnd is not null:
                    state.RangeTombstones[identity] = state.RangeTombstones[identity].Add(
                        new CommittedRangeTombstone(
                            mutation.Key.ToArray(),
                            mutation.RangeEnd.ToArray(),
                            checked((long)mutation.Sequence)));
                    foreach (var key in family.Keys.Where(key =>
                                 ByteArrayComparer.Instance.Compare(key, mutation.Key) >= 0 &&
                                 ByteArrayComparer.Instance.Compare(key, mutation.RangeEnd) < 0).ToList())
                    {
                        family = family.Remove(key);
                    }

                    break;
            }

            state.FamilyData[identity] = family;
        }
    }

    FileMeta CreateSst(
        uint familyId,
        uint level,
        IReadOnlyList<SstEntry> entries,
        IReadOnlyList<RangeTombstone> ranges,
        Failpoint outputDurableFailpoint,
        ulong? assignedSequence = null)
    {
        var output = StageSst(
            familyId,
            level,
            entries,
            ranges,
            outputDurableFailpoint,
            assignedSequence,
            $"{_lease.Epoch}.compaction");
        _lease.EnsureValid();
        FinalizeStagedSst(output);
        return output.Metadata;
    }

    StagedSstOutput StageSst(
        uint familyId,
        uint level,
        IReadOnlyList<SstEntry> entries,
        IReadOnlyList<RangeTombstone> ranges,
        Failpoint outputDurableFailpoint,
        ulong? assignedSequence,
        string? stagingIdentity)
    {
        var manifest = Volatile.Read(ref _manifestReadSnapshot);
        var sequence = assignedSequence ??
                       (manifest.NextSstSequences.TryGetValue(familyId, out var nextSequence)
                           ? nextSequence
                           : 1UL);
        var name = CreateSstFileName(familyId, level, sequence);
        var stagingPrefix = string.IsNullOrEmpty(stagingIdentity)
            ? _lease.Epoch.ToString(CultureInfo.InvariantCulture)
            : stagingIdentity;
        var stagingPath = Path.Combine(
            _sstDirectory,
            ".flush-staging",
            $"{stagingPrefix}.{name}.tmp");
        var finalPath = Path.Combine(_sstDirectory, name);
        long sizeBytes;
        uint contentCrc32C;
        using (var stream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var checkedStream = new Crc32CWriteStream(stream);
            SstCodec.EncodeTo(checkedStream, entries, ranges, _performanceGoal);
            stream.Flush(true);
            sizeBytes = checkedStream.BytesWritten;
            contentCrc32C = checkedStream.Checksum;
        }

        Interlocked.Add(ref _sstBytesWrittenTotal, sizeBytes);

        _failpoints.Hit(outputDurableFailpoint);
        var allKeys = entries.Select(entry => entry.Key)
            .Concat(ranges.SelectMany(range => new[] { range.Start, range.End }))
            .OrderBy(key => key, ByteArrayComparer.Instance)
            .ToList();
        var allSequences = entries.Select(entry => entry.Sequence).Concat(ranges.Select(range => range.Sequence))
            .ToList();
        var metadata = new FileMeta
        {
            Name = name,
            Level = level,
            SizeBytes = checked((ulong)sizeBytes),
            ContentCrc32C = contentCrc32C,
            ColumnFamilyId = familyId,
            SstSequence = sequence,
            SmallestKey = allKeys.Count == 0 ? null : allKeys[0].Select(value => (int)value).ToArray(),
            LargestKey = allKeys.Count == 0 ? null : allKeys[^1].Select(value => (int)value).ToArray(),
            SmallestSequence = allSequences.Count == 0 ? null : allSequences.Min(),
            LargestSequence = allSequences.Count == 0 ? null : allSequences.Max(),
            Sublevel = 0
        };
        return new StagedSstOutput(metadata, stagingPath, finalPath);
    }

    static void FinalizeStagedSst(StagedSstOutput output)
    {
        if (File.Exists(output.FinalPath))
        {
            ValidateFinalizedSst(output);
            if (File.Exists(output.StagingPath))
            {
                var existing = PositionalFile.ReadAllBytes(output.FinalPath);
                var staged = PositionalFile.ReadAllBytes(output.StagingPath);
                if (!existing.AsSpan().SequenceEqual(staged))
                {
                    throw new PantsCorruptionException(
                        $"Unpublished SST residue '{output.Metadata.Name}' conflicts with the retry output.");
                }

                File.Delete(output.StagingPath);
            }

            return;
        }

        File.Move(output.StagingPath, output.FinalPath, false);
        ValidateFinalizedSst(output);
    }

    static void ValidatePublishedFlushOutput(
        FrozenMemtableFlush frozen,
        FileMeta published,
        FlushPublicationPlan plan)
    {
        var output = AssertSingleFlushOutput(frozen, plan);
        if (!HasSameFlushMetadata(published, output.Metadata))
        {
            throw new PantsCorruptionException(
                $"Published SST '{frozen.SstName}' does not match its retained immutable memtable.");
        }

        if (!File.Exists(output.FinalPath))
        {
            throw new PantsCorruptionException(
                $"Published SST '{frozen.SstName}' is missing from local storage.");
        }

        ValidateFinalizedSst(output);
        if (File.Exists(output.StagingPath))
        {
            var publishedBytes = PositionalFile.ReadAllBytes(output.FinalPath);
            var stagedBytes = PositionalFile.ReadAllBytes(output.StagingPath);
            if (!publishedBytes.AsSpan().SequenceEqual(stagedBytes))
            {
                throw new PantsCorruptionException(
                    $"Published SST '{frozen.SstName}' content does not match its retry output.");
            }

            File.Delete(output.StagingPath);
        }
    }

    static void ValidateFinalizedSst(StagedSstOutput output)
    {
        var bytes = PositionalFile.ReadAllBytes(output.FinalPath);
        if (output.Metadata.SizeBytes != checked((ulong)bytes.Length) ||
            output.Metadata.ContentCrc32C != DiskFormat.Crc32C(bytes))
        {
            throw new PantsCorruptionException(
                $"Published SST '{output.Metadata.Name}' content does not match its build output.");
        }

        _ = SstCodec.Decode(bytes);
    }

    static StagedSstOutput AssertSingleFlushOutput(
        FrozenMemtableFlush frozen,
        FlushPublicationPlan plan)
    {
        if (plan.Outputs.Count != 1 ||
            plan.Outputs[0].Metadata.Name != frozen.SstName ||
            plan.Outputs[0].Metadata.ColumnFamilyId != frozen.ColumnFamilyId)
        {
            throw new PantsCorruptionException(
                $"Flush retry '{frozen.Id}' produced an inconsistent SST publication plan.");
        }

        return plan.Outputs[0];
    }

    static bool HasSameFlushMetadata(FileMeta left, FileMeta right) =>
        left.Name == right.Name &&
        left.Level == right.Level &&
        left.SizeBytes == right.SizeBytes &&
        left.ContentCrc32C == right.ContentCrc32C &&
        left.ColumnFamilyId == right.ColumnFamilyId &&
        left.SstSequence == right.SstSequence &&
        left.SmallestSequence == right.SmallestSequence &&
        left.LargestSequence == right.LargestSequence &&
        left.Sublevel == right.Sublevel &&
        HasSameKey(left.SmallestKey, right.SmallestKey) &&
        HasSameKey(left.LargestKey, right.LargestKey);

    static bool HasSameKey(int[]? left, int[]? right) =>
        left is null
            ? right is null
            : right is not null && left.AsSpan().SequenceEqual(right);

    static Dictionary<string, object?> CreateIntentFileMetadata(FileMeta metadata) => new()
    {
        ["name"] = metadata.Name,
        ["level"] = metadata.Level,
        ["size_bytes"] = metadata.SizeBytes,
        ["content_crc32c"] = metadata.ContentCrc32C,
        ["cf_id"] = metadata.ColumnFamilyId,
        ["smallest_key"] = metadata.SmallestKey,
        ["largest_key"] = metadata.LargestKey,
        ["smallest_seq"] = metadata.SmallestSequence,
        ["largest_seq"] = metadata.LargestSequence
    };

    static JsonElement CreateIntentEntry(string variant, object value) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [variant] = value },
            JsonOptions);

    void SaveIntentLog(List<JsonElement> intents)
    {
        lock (_manifestGate)
        {
            AtomicStagedFile.Write(
                _intentPath,
                JsonSerializer.SerializeToUtf8Bytes(intents, JsonOptions),
                beforePublish: () => _failpoints.Hit(Failpoint.BeforeIntentLogReplace));
            _failpoints.Hit(Failpoint.AfterIntentLogReplace);
        }
    }

    void UpsertFlushIntents(FlushPublicationPlan plan)
    {
        lock (_manifestGate)
        {
            var outputNames = plan.Outputs
                .Select(static output => output.Metadata.Name)
                .ToHashSet(StringComparer.Ordinal);
            var retained = LoadIntentLog()
                .Where(intent => !IsTargetFlushIntent(intent, outputNames))
                .ToList();
            retained.AddRange(plan.Intents);
            SaveIntentLog(retained);
        }
    }

    void RemoveFlushIntents(IEnumerable<string> names)
    {
        lock (_manifestGate)
        {
            var targetNames = names.ToHashSet(StringComparer.Ordinal);
            var retained = LoadIntentLog()
                .Where(intent => !IsTargetFlushIntent(intent, targetNames))
                .ToList();
            SaveIntentLog(retained);
        }
    }

    string[] UpsertCompactionIntents(List<JsonElement> intents)
    {
        if (intents.Count == 0)
        {
            return [];
        }

        lock (_manifestGate)
        {
            var retained = LoadIntentLog();
            var supersededOutputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var intent in intents)
            {
                var identity = GetCompactionIntentIdentity(intent) ??
                               throw new PantsCorruptionException(
                                   "A new compaction publication intent is malformed.");
                foreach (var existing in retained)
                {
                    if (GetCompactionIntentIdentity(existing) is not { } existingIdentity)
                    {
                        continue;
                    }

                    existingIdentity.ValidateReplacement(
                        identity,
                        GetCompactionIntentPhase(existing) ?? string.Empty);
                }

                _ = retained.RemoveAll(existing =>
                {
                    if (GetCompactionIntentPhase(existing) != "OutputDurable" ||
                        GetCompactionIntentIdentity(existing) is not { } existingIdentity ||
                        (!existingIdentity.HasSameInputs(identity) &&
                         !existingIdentity.HasSameOutputs(identity)))
                    {
                        return false;
                    }

                    foreach (var name in existingIdentity.GetAddedFileNames()
                                 .Where(name => !identity.GetAddedFileNames().Contains(
                                     name,
                                     StringComparer.Ordinal)))
                    {
                        supersededOutputs.Add(ValidateSstName(name));
                    }

                    return true;
                });
                retained.Add(intent);
            }

            SaveIntentLog(retained);
            return supersededOutputs.Order(StringComparer.Ordinal).ToArray();
        }
    }

    void DeleteSupersededCompactionOutputs(string[] names)
    {
        if (names.Length == 0)
        {
            return;
        }

        foreach (var name in names)
        {
            _lease.EnsureValid();
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
        }

        AtomicStagedFile.FlushDirectory(_sstDirectory);
        _lease.EnsureValid();
    }

    void RemoveCompactionIntents(List<JsonElement> intents)
    {
        if (intents.Count == 0)
        {
            return;
        }

        lock (_manifestGate)
        {
            var identities = intents
                .Select(GetCompactionIntentIdentity)
                .Where(static identity => identity is not null)
                .Cast<CompactionIntentIdentity>()
                .ToHashSet();
            var retained = LoadIntentLog()
                .Where(intent => GetCompactionIntentIdentity(intent) is not { } identity ||
                                 !identities.Contains(identity))
                .ToList();
            SaveIntentLog(retained);
        }
    }

    void TransitionCompactionIntents(List<JsonElement> intents, string phase)
    {
        if (intents.Count == 0)
        {
            return;
        }

        lock (_manifestGate)
        {
            var identities = intents
                .Select(GetCompactionIntentIdentity)
                .Where(static identity => identity is not null)
                .Cast<CompactionIntentIdentity>()
                .ToHashSet();
            var transitioned = 0;
            var retained = LoadIntentLog();
            for (var index = 0; index < retained.Count; index++)
            {
                if (GetCompactionIntentIdentity(retained[index]) is not { } identity ||
                    !identities.Contains(identity))
                {
                    continue;
                }

                retained[index] = SetCompactionIntentPhase(retained[index], phase);
                transitioned = checked(transitioned + 1);
            }

            if (transitioned != identities.Count)
            {
                throw new PantsCorruptionException(
                    $"Expected {identities.Count} compaction intent transitions, found {transitioned}.");
            }

            SaveIntentLog(retained);
        }
    }

    List<JsonElement> LoadIntentLog()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_intentPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The intent log root must be an array.");
            }

            return document.RootElement
                .EnumerateArray()
                .Select(static intent => intent.Clone())
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new PantsCorruptionException(
                "The intent log could not be read while publishing a flush.",
                exception);
        }
    }

    bool HasManifestPublishedCompactionIntent() => LoadIntentLog().Any(intent =>
        GetCompactionIntentPhase(intent) == "ManifestPublished");

    static bool IsTargetFlushIntent(JsonElement intent, HashSet<string> targetNames)
    {
        if (intent.ValueKind != JsonValueKind.Object ||
            intent.EnumerateObject().Count() != 1)
        {
            return false;
        }

        var variant = intent.EnumerateObject().Single();
        if (variant.Name is not ("FlushPublish" or "SstAdded"))
        {
            return false;
        }

        var value = variant.Value;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var metadata = value.TryGetProperty("file_meta", out var nestedMetadata)
            ? nestedMetadata
            : value;
        return metadata.ValueKind == JsonValueKind.Object &&
               metadata.TryGetProperty("name", out var name) &&
               name.ValueKind == JsonValueKind.String &&
               targetNames.Contains(name.GetString()!);
    }

    static CompactionIntentIdentity? GetCompactionIntentIdentity(JsonElement intent)
    {
        if (intent.ValueKind != JsonValueKind.Object ||
            intent.EnumerateObject().Count() != 1)
        {
            return null;
        }

        var variant = intent.EnumerateObject().Single();
        if (variant.Name is not ("CompactionPublish" or "CompactionApplied") ||
            variant.Value.ValueKind != JsonValueKind.Object ||
            !variant.Value.TryGetProperty("cf_id", out var familyId) ||
            !familyId.TryGetUInt32(out var parsedFamilyId) ||
            !variant.Value.TryGetProperty("removed", out var removed) ||
            removed.ValueKind != JsonValueKind.Array ||
            !variant.Value.TryGetProperty("added", out var added) ||
            added.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var removedNames = new List<string>();
        foreach (var item in removed.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            removedNames.Add(item.GetString()!);
        }

        var addedNames = new List<string>();
        foreach (var item in added.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            addedNames.Add(name.GetString()!);
        }

        return CompactionIntentIdentity.Create(parsedFamilyId, removedNames, addedNames);
    }

    static string? GetCompactionIntentPhase(JsonElement intent)
    {
        if (intent.ValueKind != JsonValueKind.Object ||
            intent.EnumerateObject().Count() != 1)
        {
            return null;
        }

        var variant = intent.EnumerateObject().Single();
        if (variant.Name == "CompactionApplied")
        {
            return "ManifestPublished";
        }

        return variant.Name == "CompactionPublish" &&
               variant.Value.ValueKind == JsonValueKind.Object &&
               variant.Value.TryGetProperty("phase", out var phase) &&
               phase.ValueKind == JsonValueKind.String
            ? phase.GetString()
            : null;
    }

    static JsonElement SetCompactionIntentPhase(JsonElement intent, string phase)
    {
        var variant = intent.EnumerateObject().Single();
        var value = variant.Value.EnumerateObject().ToDictionary(
            static property => property.Name,
            property => property.Name == "phase"
                ? JsonSerializer.SerializeToElement(phase)
                : property.Value.Clone(),
            StringComparer.Ordinal);
        value["phase"] = JsonSerializer.SerializeToElement(phase);
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [variant.Name] = value },
            JsonOptions);
    }

    void ClearIntentLog()
    {
        lock (_manifestGate)
        {
            AtomicStagedFile.Write(_intentPath, "[]"u8);
        }
    }

    static JsonElement CreateManifestEdit(string variant, object value) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [variant] = value },
            JsonOptions);

    void DurablyApplyManifestBatch(List<JsonElement> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        DurablyApplyManifestEdit(CreateManifestEdit("Batch", edits));
    }

    void DurablyApplyManifestEdit(JsonElement edit)
    {
        lock (_manifestGate)
        {
            DurablyApplyManifestEditCore(edit);
        }
    }

    void DurablyApplyManifestEditCore(JsonElement edit)
    {
        var recordType = GetManifestEditRecordType(edit);
        var editId = checked(_manifest.EditCheckpointId + 1);
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteNumber("edit_id", editId);
            writer.WritePropertyName("edit");
            edit.WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
            payload = buffer.ToArray();
        }

        var record = EncodeManifestJournalRecord(recordType, payload);
        var markerPayload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                last_persisted_sequence = editId,
                ts_millis = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            },
            JsonOptions);
        var marker = EncodeManifestJournalRecord(9, markerPayload);
        _failpoints.Hit(Failpoint.BeforeManifestJournalAppend);
        PositionalFile.AppendAndFlush(
            _manifestJournalPath,
            [record, marker],
            () => _failpoints.Hit(Failpoint.AfterManifestJournalAppend),
            () => _failpoints.Hit(Failpoint.BeforeManifestJournalSync),
            () => _failpoints.Hit(Failpoint.AfterManifestJournalSync));

        ApplyManifestEdit(_manifest, edit, recordType);
        _manifest.EditCheckpointId = editId;
        RefreshManifestReadSnapshot();
    }

    static byte[] EncodeManifestJournalRecord(byte recordType, ReadOnlySpan<byte> payload)
    {
        var record = GC.AllocateUninitializedArray<byte>(checked(1 + sizeof(uint) + payload.Length + sizeof(uint)));
        record[0] = recordType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), checked((uint)payload.Length));
        payload.CopyTo(record.AsSpan(1 + sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(1 + sizeof(uint) + payload.Length),
            Crc32(payload));
        return record;
    }

    void SaveManifestCheckpoint()
    {
        lock (_manifestGate)
        {
            SaveManifestCheckpointCore();
        }
    }

    void SaveManifestCheckpointCore()
    {
        RefreshManifestReadSnapshot();
        var json = JsonSerializer.SerializeToUtf8Bytes(_manifest, JsonOptions);
        AtomicStagedFile.Write(
            _manifestSnapshotPath,
            json,
            beforePublish: () => _failpoints.Hit(Failpoint.BeforeManifestCheckpointReplace));
        AtomicStagedFile.Write(_manifestJournalPath, []);
        AtomicStagedFile.Write(_manifestPath, json);
        _failpoints.Hit(Failpoint.AfterManifestCheckpointReplace);
    }

    void RefreshManifestReadSnapshot() =>
        Volatile.Write(ref _manifestReadSnapshot, ManifestReadSnapshot.Create(_manifest));

    void RotateWal()
    {
        ThrowIfWalWriteFailed();
        _failpoints.Hit(Failpoint.BeforeWalRotation);
        _walStream.Dispose();
        using (var truncate = new FileStream(_walPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            truncate.Flush(true);
        }

        _walStream = new FileStream(_walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _walRecords = 0;
        _walPendingWrites = 0;
        foreach (var sealedSegment in EnumerateSealedWalSegmentPaths(_walDirectory))
        {
            File.Delete(sealedSegment);
        }

        _failpoints.Hit(Failpoint.AfterWalRotation);
    }

    uint ResolveFamilyId(ColumnFamilyIdentity identity) =>
        _familyIds.TryGetValue(identity, out var id)
            ? id
            : throw new StorageException($"Column family '{identity}' has no persistent Midge identity.");

    internal static bool IsWithinFileRange(FileMeta file, ReadOnlySpan<byte> key)
    {
        if (file.SmallestKey is null || file.LargestKey is null)
        {
            return false;
        }

        var smallest = file.SmallestKey.Select(static value => checked((byte)value)).ToArray();
        var largest = file.LargestKey.Select(static value => checked((byte)value)).ToArray();
        return key.SequenceCompareTo(smallest) >= 0 && key.SequenceCompareTo(largest) <= 0;
    }

    static bool OverlapsFileRange(FileMeta file, ReadOnlySpan<byte> start, ReadOnlySpan<byte> end)
    {
        if (file.SmallestKey is null || file.LargestKey is null)
        {
            return false;
        }

        var smallest = file.SmallestKey.Select(static value => checked((byte)value)).ToArray();
        var largest = file.LargestKey.Select(static value => checked((byte)value)).ToArray();
        return largest.AsSpan().SequenceCompareTo(start) >= 0 &&
               smallest.AsSpan().SequenceCompareTo(end) < 0;
    }

    static bool RangesOverlap(
        ReadOnlySpan<byte> leftStart,
        ReadOnlySpan<byte> leftEnd,
        ReadOnlySpan<byte> rightStart,
        ReadOnlySpan<byte> rightEnd) =>
        leftStart.SequenceCompareTo(rightEnd) < 0 && rightStart.SequenceCompareTo(leftEnd) < 0;

    static void EnsureFormat(string root)
    {
        var path = Path.Combine(root, "FORMAT");
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual("midge-format-version=3\n"u8))
            {
                throw new PantsCompatibilityException("Unsupported or invalid Midge FORMAT marker.");
            }

            return;
        }

        var hasState = Directory.EnumerateFileSystemEntries(root)
            .Select(Path.GetFileName)
            .Any(static name => name is not (
                                    "FORMAT.tmp" or
                                    "LOCK" or
                                    ".midge_leader" or
                                    ".midge_leader.lock") &&
                                !name!.StartsWith(".midge_leader.", StringComparison.Ordinal));
        if (hasState)
        {
            throw new PantsCompatibilityException(
                "Persisted state without a Midge FORMAT marker is unsupported.");
        }

        AtomicStagedFile.Write(path, "midge-format-version=3\n"u8);
    }

    static ManifestLoadResult LoadManifest(
        string root,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var snapshot = Path.Combine(root, "manifest.snapshot.json");
        var legacy = Path.Combine(root, "manifest.json");
        var sources = new[] { snapshot, legacy }.Where(File.Exists).ToArray();
        if (sources.Length == 0)
        {
            return new ManifestLoadResult(ManifestState.CreateInitial(), false);
        }

        Exception? failure = null;
        foreach (var source in sources)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ManifestState>(
                    File.ReadAllBytes(source),
                    JsonOptions) ?? throw new JsonException("Midge manifest is empty.");
                if (failure is not null)
                {
                    state.MarkSalvageMode();
                }

                return new ManifestLoadResult(manifest, false);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                failure = exception;
                if (recoveryPolicy == PantsRecoveryPolicy.Strict)
                {
                    throw PantsException.Create(
                        PantsErrorCode.RecoveryFailed,
                        "The manifest could not be recovered strictly.",
                        exception);
                }

                state.MarkSalvageMode();
                RetainCorruptFile(source);
            }
        }

        return new ManifestLoadResult(ReconstructManifestFromSsts(root), true);
    }

    static ManifestState ReconstructManifestFromSsts(string root)
    {
        var manifest = ManifestState.CreateInitial();
        var sstDirectory = Path.Combine(root, "sst");
        foreach (var path in Directory.EnumerateFiles(sstDirectory, "*.sst", SearchOption.TopDirectoryOnly))
        {
            var name = ValidateSstName(Path.GetFileName(path));
            var stem = name[..^4];
            var parts = stem.Split('_');
            if (parts.Length != 3 ||
                !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var familyId) ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
                !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sstSequence))
            {
                continue;
            }

            try
            {
                var bytes = PositionalFile.ReadAllBytes(path);
                var contents = SstCodec.Decode(bytes);
                var keys = contents.Entries.Select(static entry => entry.Key)
                    .Concat(contents.RangeTombstones.SelectMany(static range => new[] { range.Start, range.End }))
                    .OrderBy(static key => key, ByteArrayComparer.Instance)
                    .ToList();
                var sequences = contents.Entries.Select(static entry => entry.Sequence)
                    .Concat(contents.RangeTombstones.Select(static range => range.Sequence))
                    .ToList();
                manifest.Files.Add(new FileMeta
                {
                    Name = name,
                    Level = level,
                    SizeBytes = checked((ulong)bytes.Length),
                    ContentCrc32C = DiskFormat.Crc32C(bytes),
                    ColumnFamilyId = familyId,
                    SstSequence = sstSequence,
                    SmallestKey = keys.Count == 0 ? null : keys[0].Select(static value => (int)value).ToArray(),
                    LargestKey = keys.Count == 0 ? null : keys[^1].Select(static value => (int)value).ToArray(),
                    SmallestSequence = sequences.Count == 0 ? null : sequences.Min(),
                    LargestSequence = sequences.Count == 0 ? null : sequences.Max(),
                    Sublevel = 0
                });
                manifest.LastPersistedSequence = Math.Max(
                    manifest.LastPersistedSequence,
                    sequences.Count == 0 ? 0 : sequences.Max());
                manifest.NextSstSeqs[familyId] = Math.Max(
                    manifest.NextSstSeqs.GetValueOrDefault(familyId, 1UL),
                    checked(sstSequence + 1));
            }
            catch (Exception exception) when (exception is IOException or PantsException or OverflowException)
            {
                // Salvage preserves undecodable SSTs without claiming them as readable manifest state.
            }
        }

        return manifest;
    }

    static void ValidateManifestSstNames(ManifestState manifest)
    {
        try
        {
            foreach (var file in manifest.Files)
            {
                ValidateSstName(file.Name);
            }

            foreach (var family in manifest.ColumnFamilies)
            {
                foreach (var name in family.DroppedSstNames)
                {
                    ValidateSstName(name);
                }
            }

            if (manifest.CloudCheckpoint is JsonElement checkpoint &&
                checkpoint.ValueKind == JsonValueKind.Object &&
                checkpoint.TryGetProperty("covering_ssts", out var coveringSsts) &&
                coveringSsts.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in coveringSsts.EnumerateArray())
                {
                    ValidateSstName(name.GetString() ?? string.Empty);
                }
            }
        }
        catch (StorageException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "The manifest contains an unsafe SST name.",
                exception);
        }
    }

    static RecoveryMetadataResult ValidateRecoveryMetadata(
        string root,
        ManifestState manifest,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state,
        StartupPhaseRecorder startupPhases,
        IFailpointHandler failpoints)
    {
        var journalPath = Path.Combine(root, "manifest.journal");
        using (startupPhases.Measure(StartupPhase.ManifestJournal))
        {
            byte[] journalBytes;
            int durableByteLength;
            try
            {
                journalBytes = File.ReadAllBytes(journalPath);
                ReplayManifestJournal(journalBytes, manifest, out durableByteLength);
            }
            catch (Exception exception) when (exception is PantsException or IOException)
            {
                RecoverMetadataFile(
                    journalPath,
                    "The manifest journal could not be recovered strictly.",
                    [],
                    recoveryPolicy,
                    state,
                    exception);
                journalBytes = [];
                durableByteLength = 0;
            }

            if (durableByteLength < journalBytes.Length)
            {
                try
                {
                    AtomicStagedFile.Write(
                        journalPath,
                        journalBytes.AsSpan(0, durableByteLength),
                        beforePublish: () => failpoints.Hit(Failpoint.BeforeManifestJournalRepairReplace),
                        temporaryFileName: "manifest.journal.repair.tmp");
                }
                catch (IOException)
                {
                    // The durable prefix was already replayed. Checkpoint publication later in
                    // Open retries the repair without misclassifying valid journal content.
                }
            }
        }

        var intentPath = Path.Combine(root, "intent_log.json");
        using (startupPhases.Measure(StartupPhase.IntentReconciliation))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(intentPath));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("The intent log root must be an array.");
                }

                var clearRecoveredIntents = ReplayIntentLog(
                    root,
                    manifest,
                    document.RootElement,
                    recoveryPolicy,
                    state);
                return new RecoveryMetadataResult(clearRecoveredIntents, !clearRecoveredIntents &&
                                                                         document.RootElement.GetArrayLength() != 0);
            }
            catch (Exception exception) when (exception is JsonException or PantsException or IOException)
            {
                RecoverMetadataFile(
                    intentPath,
                    "The intent log could not be recovered strictly.",
                    "[]"u8.ToArray(),
                    recoveryPolicy,
                    state,
                    exception);
                return new RecoveryMetadataResult(false, true);
            }
        }
    }

    static bool ReplayIntentLog(
        string root,
        ManifestState manifest,
        JsonElement intentLog,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var entryCount = intentLog.GetArrayLength();
        if (entryCount == 0)
        {
            return false;
        }

        state.RecordIntentLogReplay(entryCount);
        var hasEntries = false;
        var safeToClear = true;
        foreach (var entry in intentLog.EnumerateArray())
        {
            hasEntries = true;
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 1)
            {
                safeToClear &= HandleIntentRecoveryIssue(
                    "The intent log contains a malformed entry.",
                    recoveryPolicy,
                    state);
                continue;
            }

            var variant = entry.EnumerateObject().Single();
            switch (variant.Name)
            {
                case "FlushPublish":
                    safeToClear &= ReplayFlushIntent(
                        root,
                        manifest,
                        variant.Value,
                        GetRequiredString(variant.Value, "phase"),
                        recoveryPolicy,
                        state);
                    break;
                case "SstAdded":
                    safeToClear &= ReplayFlushIntent(
                        root,
                        manifest,
                        variant.Value,
                        "ManifestPublished",
                        recoveryPolicy,
                        state);
                    break;
                case "CompactionPublish":
                    safeToClear &= ReplayCompactionIntent(
                        root,
                        manifest,
                        variant.Value,
                        GetRequiredString(variant.Value, "phase"),
                        recoveryPolicy,
                        state);
                    break;
                case "CompactionApplied":
                    safeToClear &= ReplayCompactionIntent(
                        root,
                        manifest,
                        variant.Value,
                        "ManifestPublished",
                        recoveryPolicy,
                        state);
                    break;
                case "CompactionPlanned":
                    foreach (var name in GetStringArray(variant.Value, "input_files"))
                    {
                        _ = ValidateSstName(name);
                    }

                    break;
                case "SeqnoAllocated":
                case "FlushPlanned":
                case "WalSynced":
                case "CloudUploadComplete":
                    break;
                default:
                    safeToClear &= HandleIntentRecoveryIssue(
                        $"The intent log entry '{variant.Name}' is unsupported.",
                        recoveryPolicy,
                        state);
                    break;
            }
        }

        return hasEntries && safeToClear;
    }

    static bool ReplayFlushIntent(
        string root,
        ManifestState manifest,
        JsonElement intent,
        string phase,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var metadataElement = intent.TryGetProperty("file_meta", out var fileMetadata)
            ? fileMetadata
            : intent;
        var metadata = ParseIntentFileMetadata(metadataElement);
        if (manifest.Files.Any(file => file.Name == metadata.Name))
        {
            return true;
        }

        return phase switch
        {
            "OutputDurable" => DeleteUnpublishedIntentSst(
                root,
                metadata,
                recoveryPolicy,
                state),
            "ManifestPublished" => PublishRecoveredIntentSst(
                root,
                manifest,
                metadata,
                recoveryPolicy,
                state),
            _ => HandleIntentRecoveryIssue(
                $"Flush publication intent has unknown phase '{phase}'.",
                recoveryPolicy,
                state)
        };
    }

    static bool ReplayCompactionIntent(
        string root,
        ManifestState manifest,
        JsonElement intent,
        string phase,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var removed = GetStringArray(intent, "removed");
        if (removed.Length == 0)
        {
            return HandleIntentRecoveryIssue(
                "Compaction publication intent has no inputs.",
                recoveryPolicy,
                state);
        }

        foreach (var name in removed)
        {
            _ = ValidateSstName(name);
        }

        if (!intent.TryGetProperty("added", out var addedElement) ||
            addedElement.ValueKind != JsonValueKind.Array)
        {
            return HandleIntentRecoveryIssue(
                "Compaction publication intent has invalid outputs.",
                recoveryPolicy,
                state);
        }

        var added = addedElement
            .EnumerateArray()
            .Select(ParseIntentFileMetadata)
            .ToArray();
        var columnFamilyId = GetRequiredUInt32(intent, "cf_id");
        var columnFamilyInactive = manifest.ColumnFamilies.All(family =>
            family.Id != columnFamilyId || family.DeletedAt.HasValue);
        var allInputsPresent = removed.All(name => manifest.Files.Any(file => file.Name == name));
        var allInputsAbsent = removed.All(name => manifest.Files.All(file => file.Name != name));
        var allOutputsPresent = added.All(output => manifest.Files.Any(file => file.Name == output.Name));
        var allOutputsAbsent = added.All(output => manifest.Files.All(file => file.Name != output.Name));

        if (phase == "OutputDurable" && columnFamilyInactive && allOutputsAbsent)
        {
            return added.All(output => DeleteUnpublishedIntentSst(
                root,
                output,
                recoveryPolicy,
                state));
        }

        if (phase == "OutputDurable" && allInputsPresent && allOutputsAbsent)
        {
            return added.All(output => DeleteUnpublishedIntentSst(
                root,
                output,
                recoveryPolicy,
                state));
        }

        if (phase is "OutputDurable" or "ManifestPublished" &&
            allInputsAbsent &&
            allOutputsPresent)
        {
            if (!added.All(output => ValidateIntentSst(root, output, recoveryPolicy, state)))
            {
                return false;
            }

            return removed.All(name => DeleteRecoveredSst(
                root,
                name,
                recoveryPolicy,
                state));
        }

        if (phase == "ManifestPublished" && allInputsPresent && allOutputsAbsent)
        {
            if (!added.All(output => ValidateIntentSst(root, output, recoveryPolicy, state)))
            {
                return false;
            }

            manifest.Files.RemoveAll(file => removed.Contains(file.Name, StringComparer.Ordinal));
            manifest.Files.AddRange(added);
            if (!removed.All(name => DeleteRecoveredSst(root, name, recoveryPolicy, state)))
            {
                return false;
            }

            return true;
        }

        return HandleIntentRecoveryIssue(
            $"Compaction publication intent has partial manifest visibility (phase={phase}).",
            recoveryPolicy,
            state);
    }

    static FileMeta ParseIntentFileMetadata(JsonElement element)
    {
        var name = ValidateSstName(GetRequiredString(element, "name"));
        return new FileMeta
        {
            Name = name,
            Level = GetRequiredUInt32(element, "level"),
            SizeBytes = GetRequiredUInt64(element, "size_bytes"),
            ContentCrc32C = GetOptionalUInt32(element, "content_crc32c"),
            ColumnFamilyId = GetRequiredUInt32(element, "cf_id"),
            SmallestKey = GetOptionalByteArray(element, "smallest_key"),
            LargestKey = GetOptionalByteArray(element, "largest_key"),
            SmallestSequence = GetOptionalUInt64(element, "smallest_seq"),
            LargestSequence = GetOptionalUInt64(element, "largest_seq")
        };
    }

    static bool PublishRecoveredIntentSst(
        string root,
        ManifestState manifest,
        FileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        if (!ValidateIntentSst(root, metadata, recoveryPolicy, state))
        {
            return false;
        }

        manifest.Files.Add(metadata);
        return true;
    }

    static bool DeleteUnpublishedIntentSst(
        string root,
        FileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var path = Path.Combine(root, "sst", metadata.Name);
        if (!File.Exists(path))
        {
            return true;
        }

        return ValidateIntentSst(root, metadata, recoveryPolicy, state) &&
               DeleteRecoveredSst(root, metadata.Name, recoveryPolicy, state);
    }

    static bool ValidateIntentSst(
        string root,
        FileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        var path = Path.Combine(root, "sst", metadata.Name);
        try
        {
            var bytes = PositionalFile.ReadAllBytes(path);
            if ((metadata.SizeBytes != 0 && metadata.SizeBytes != checked((ulong)bytes.Length)) ||
                (metadata.ContentCrc32C.HasValue &&
                 metadata.ContentCrc32C.Value != DiskFormat.Crc32C(bytes)))
            {
                return HandleIntentRecoveryIssue(
                    $"Recovery intent SST '{metadata.Name}' does not match its publication proof.",
                    recoveryPolicy,
                    state);
            }

            _ = SstCodec.Decode(bytes);
            return true;
        }
        catch (Exception exception) when (exception is IOException or PantsException)
        {
            return HandleIntentRecoveryIssue(
                $"Recovery intent references invalid SST '{metadata.Name}': {exception.Message}",
                recoveryPolicy,
                state);
        }
    }

    static bool DeleteRecoveredSst(
        string root,
        string name,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        try
        {
            File.Delete(Path.Combine(root, "sst", ValidateSstName(name)));
            return true;
        }
        catch (IOException exception)
        {
            return HandleIntentRecoveryIssue(
                $"Recovery could not delete SST '{name}': {exception.Message}",
                recoveryPolicy,
                state);
        }
    }

    static bool HandleIntentRecoveryIssue(
        string message,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state)
    {
        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            throw PantsException.Create(PantsErrorCode.RecoveryFailed, message);
        }

        state.MarkSalvageMode();
        return false;
    }

    static void RecoverMetadataFile(
        string path,
        string strictMessage,
        byte[] replacement,
        PantsRecoveryPolicy recoveryPolicy,
        RuntimeState state,
        Exception exception)
    {
        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            throw PantsException.Create(PantsErrorCode.RecoveryFailed, strictMessage, exception);
        }

        state.MarkSalvageMode();
        RetainCorruptFile(path);
        AtomicStagedFile.Write(path, replacement);
    }

    internal static void ValidateManifestJournal(ReadOnlySpan<byte> bytes) =>
        _ = ReadDurableJournalRecords(bytes, out _);

    static void ReplayManifestJournal(ReadOnlySpan<byte> bytes, ManifestState manifest, out int durableByteLength)
    {
        var records = ReadDurableJournalRecords(bytes, out durableByteLength);
        var nextLegacyEditId = manifest.EditCheckpointId;
        foreach (var record in records)
        {
            using var document = JsonDocument.Parse(record.Payload);
            var edit = document.RootElement;
            ulong editId;
            if (edit.ValueKind == JsonValueKind.Object &&
                edit.TryGetProperty("edit_id", out var editIdElement) &&
                edit.TryGetProperty("edit", out var envelopedEdit))
            {
                editId = editIdElement.GetUInt64();
                edit = envelopedEdit;
            }
            else
            {
                editId = checked(++nextLegacyEditId);
            }

            nextLegacyEditId = Math.Max(nextLegacyEditId, editId);
            if (editId <= manifest.EditCheckpointId)
            {
                continue;
            }

            ApplyManifestEdit(manifest, edit, record.Type);
            manifest.EditCheckpointId = editId;
        }
    }

    static JournalRecord[] ReadDurableJournalRecords(ReadOnlySpan<byte> bytes, out int durableByteLength)
    {
        var cursor = 0;
        var pending = new List<JournalRecord>();
        var durableCount = 0;
        durableByteLength = 0;
        while (cursor < bytes.Length)
        {
            var remaining = bytes.Length - cursor;
            if (remaining < 5)
            {
                break;
            }

            var recordType = bytes[cursor];
            if (recordType is < 1 or > 11)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal contains an unknown record type.");
            }

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 1)..]);
            if (payloadLength > DiskFormat.WalMaximumRecordBytes)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal record exceeds the recovery limit.");
            }

            var recordLength = checked(1 + sizeof(uint) + (int)payloadLength + sizeof(uint));
            if (recordLength > remaining)
            {
                break;
            }

            var payload = bytes.Slice(cursor + 5, checked((int)payloadLength));
            var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + 5 + checked((int)payloadLength), sizeof(uint)));
            if (Crc32(payload) != expectedChecksum)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal record checksum does not match.");
            }

            var payloadCopy = payload.ToArray();
            try
            {
                using var _ = JsonDocument.Parse(payloadCopy);
            }
            catch (JsonException exception)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal payload cannot be decoded.",
                    exception);
            }

            if (recordType == 9)
            {
                durableCount = pending.Count;
                durableByteLength = cursor + recordLength;
            }
            else
            {
                pending.Add(new JournalRecord(recordType, payloadCopy));
            }

            cursor += recordLength;
        }

        return pending.Take(durableCount).ToArray();
    }

    static void ApplyManifestEdit(
        ManifestState manifest,
        JsonElement edit,
        byte recordType)
    {
        if (recordType == 8 && edit.ValueKind == JsonValueKind.Array)
        {
            foreach (var nested in edit.EnumerateArray())
            {
                ApplyManifestEdit(manifest, nested, GetManifestEditRecordType(nested));
            }

            return;
        }

        if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count() != 1)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "The manifest journal edit shape is invalid.");
        }

        var variant = edit.EnumerateObject().Single();
        var actualRecordType = GetManifestEditRecordType(variant.Name);
        if (actualRecordType != recordType)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "The manifest journal record type does not match its edit payload.");
        }

        var value = variant.Value;
        switch (variant.Name)
        {
            case "AddSst":
                {
                    var metadata = value.Deserialize<FileMeta>(JsonOptions) ??
                                   throw PantsException.Create(PantsErrorCode.Corruption, "An AddSst edit is empty.");
                    ValidateSstName(metadata.Name);
                    manifest.Files.RemoveAll(file => file.Name == metadata.Name);
                    manifest.Files.Add(metadata);
                    break;
                }
            case "RemoveSst":
                {
                    var name = ValidateSstName(GetRequiredString(value, "name"));
                    manifest.Files.RemoveAll(file => file.Name == name);
                    break;
                }
            case "CreateColumnFamily":
                {
                    var id = GetRequiredUInt32(value, "id");
                    var name = GetRequiredString(value, "name");
                    var createdAt = GetRequiredUInt64(value, "created_at");
                    var existing = manifest.ColumnFamilies.SingleOrDefault(family => family.Id == id);
                    if (existing is null)
                    {
                        manifest.ColumnFamilies.Add(new ColumnFamilyMeta
                        {
                            Id = id,
                            Name = name,
                            CreatedAt = createdAt
                        });
                    }
                    else if (existing.DeletedAt.HasValue)
                    {
                        existing.Name = name;
                        existing.CreatedAt = createdAt;
                        existing.DeletedAt = null;
                    }

                    break;
                }
            case "DropColumnFamily":
                ApplyDropColumnFamily(manifest, GetRequiredUInt32(value, "id"), 0, []);
                break;
            case "DropColumnFamilyAt":
                ApplyDropColumnFamily(
                    manifest,
                    GetRequiredUInt32(value, "id"),
                    GetRequiredUInt64(value, "drop_sequence"),
                    GetValidatedSstNameArray(value, "dropped_sst_names"));
                break;
            case "ReclaimColumnFamily":
                {
                    var id = GetRequiredUInt32(value, "id");
                    var names = GetValidatedSstNameArray(value, "names");
                    manifest.Files.RemoveAll(file => names.Contains(file.Name, StringComparer.Ordinal));
                    var family = manifest.ColumnFamilies.SingleOrDefault(candidate => candidate.Id == id);
                    if (family is not null)
                    {
                        family.DroppedSstNames.RemoveAll(name => names.Contains(name, StringComparer.Ordinal));
                        family.Reclaimed = family.DroppedSstNames.Count == 0;
                    }

                    break;
                }
            case "BumpWalSeq":
                manifest.LastPersistedSequence = Math.Max(
                    manifest.LastPersistedSequence,
                    GetRequiredUInt64(value, "seq"));
                break;
            case "BumpNextSstSeq":
                {
                    var id = GetRequiredUInt32(value, "cf_id");
                    var sequence = GetRequiredUInt64(value, "next_seq");
                    manifest.NextSstSeqs[id] = Math.Max(
                        manifest.NextSstSeqs.GetValueOrDefault(id),
                        sequence);
                    break;
                }
            case "SetCloudCheckpoint":
                {
                    _ = GetValidatedSstNameArray(value, "covering_ssts");
                    var incomingSequence = GetRequiredUInt64(value, "checkpoint_sequence");
                    var currentSequence = manifest.CloudCheckpoint is JsonElement currentCheckpoint &&
                                          currentCheckpoint.ValueKind == JsonValueKind.Object &&
                                          currentCheckpoint.TryGetProperty("checkpoint_sequence",
                                              out var currentSequenceElement) &&
                                          currentSequenceElement.TryGetUInt64(out var currentSequenceValue)
                        ? currentSequenceValue
                        : (ulong?)null;
                    if (currentSequence is null || incomingSequence >= currentSequence.Value)
                    {
                        manifest.CloudCheckpoint = JsonSerializer.Deserialize<object>(value.GetRawText(), JsonOptions);
                    }

                    break;
                }
            case "Batch":
                foreach (var nested in value.EnumerateArray())
                {
                    ApplyManifestEdit(manifest, nested, GetManifestEditRecordType(nested));
                }

                break;
            default:
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"The manifest journal edit '{variant.Name}' is unsupported.");
        }
    }

    static void ApplyDropColumnFamily(
        ManifestState manifest,
        uint id,
        ulong dropSequence,
        string[] droppedNames)
    {
        var family = manifest.ColumnFamilies.SingleOrDefault(candidate => candidate.Id == id);
        if (family is null || family.DeletedAt.HasValue)
        {
            return;
        }

        family.DeletedAt = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        family.DropSequence = dropSequence;
        family.DroppedSstNames = droppedNames.Length == 0
            ? manifest.Files
                .Where(file => file.ColumnFamilyId == id)
                .Select(file => file.Name)
                .ToList()
            : [.. droppedNames];
        family.Reclaimed = false;
    }

    static byte GetManifestEditRecordType(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count() != 1)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest edit is malformed.");
        }

        return GetManifestEditRecordType(edit.EnumerateObject().Single().Name);
    }

    static byte GetManifestEditRecordType(string variant) => variant switch
    {
        "AddSst" => 1,
        "RemoveSst" => 2,
        "CreateColumnFamily" => 3,
        "DropColumnFamily" => 4,
        "BumpWalSeq" => 5,
        "BumpNextSstSeq" => 6,
        "SetCloudCheckpoint" => 7,
        "Batch" => 8,
        "DropColumnFamilyAt" => 10,
        "ReclaimColumnFamily" => 11,
        _ => throw PantsException.Create(
            PantsErrorCode.Corruption,
            $"The manifest edit '{variant}' is unknown.")
    };

    static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    static uint GetRequiredUInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result)
            ? result
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    static ulong GetRequiredUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt64(out var result)
            ? result
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    static uint? GetOptionalUInt32(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetUInt32(out var result)
                ? result
                : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    static ulong? GetOptionalUInt64(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetUInt64(out var result)
                ? result
                : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    static int[]? GetOptionalByteArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");
        }

        return value.EnumerateArray().Select(static item => checked((int)item.GetByte())).ToArray();
    }

    static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");
        }

        return value.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
    }

    static string[] GetValidatedSstNameArray(JsonElement element, string name) =>
        GetStringArray(element, name).Select(ValidateSstName).ToArray();

    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb8_8320;
            }
        }

        return ~crc;
    }

    static void RetainCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var retained = $"{path}.salvage-retained";
        for (var suffix = 1; File.Exists(retained); suffix++)
        {
            retained = $"{path}.salvage-retained.{suffix}";
        }

        File.Move(path, retained);
    }

    static string ValidateSstName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name != Path.GetFileName(name) ||
            !name.EndsWith(".sst", StringComparison.Ordinal) ||
            name.Contains(':') ||
            name.Contains('\\') ||
            name.Contains('\0'))
        {
            throw new StorageException($"Manifest SST name '{name}' is unsafe.");
        }

        return name;
    }

    static long GetLocalFileBytes(string directory, string pattern) =>
        GetLocalFileBytes(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));

    static long GetLocalFileBytes(IEnumerable<string> paths)
    {
        var total = 0L;
        foreach (var path in paths)
        {
            try
            {
                total = checked(total + new FileInfo(path).Length);
            }
            catch (FileNotFoundException)
            {
                // A cloud acknowledgement may remove a sealed WAL between
                // enumeration and accounting. Its bytes are no longer local.
            }
        }

        return total;
    }

    static long GetExistingFileBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    void ThrowIfWalWriteFailed()
    {
        if (Volatile.Read(ref _walWriteFailure) is { } failure)
        {
            throw new PantsAbortedException(
                "The WAL is unavailable after an uncertain commit rollback.",
                failure);
        }
    }

    enum WalReplayOutcome
    {
        Complete,
        ToleratedIncompleteTail,
        Salvaged
    }

    sealed record JournalRecord(byte Type, byte[] Payload);

    sealed record ManifestLoadResult(ManifestState Manifest, bool PreserveUnownedSsts);

    sealed record RecoveryMetadataResult(bool ClearRecoveredIntents, bool PreserveUnownedSsts);
}
