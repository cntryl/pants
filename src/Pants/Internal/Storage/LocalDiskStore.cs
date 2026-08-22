using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

internal sealed class LocalDiskStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly string _walDirectory;
    private readonly string _sstDirectory;
    private readonly string _walPath;
    private readonly string _manifestPath;
    private readonly string _manifestSnapshotPath;
    private readonly string _manifestJournalPath;
    private readonly string _intentPath;
    private readonly FileStream _lockStream;
    private readonly MidgeFileLease _lease;
    private readonly PantsRecoveryPolicy _recoveryPolicy;
    private readonly PantsPerformanceGoal _performanceGoal;
    private readonly IPantsFailpointHandler _failpoints;
    private readonly PantsCompactionConfiguration _compaction;
    private readonly long _targetSstSizeBytes;
    private readonly SstBlockCache _blockCache;
    private readonly Dictionary<ColumnFamilyIdentity, uint> _familyIds = new(ColumnFamilyIdentityComparer.Instance);
    private readonly List<MidgeWalMutation> _mutableOperations = [];
    private readonly HashSet<string> _snapshotPinnedObsoleteFiles = new(StringComparer.Ordinal);
    private readonly SstReaderCache _readerCache = new();
    private FileStream _walStream;
    private readonly MidgeManifest _manifest;
    private ulong _nextSequence;
    private ulong _unflushedCommitSequence;
    private int _walRecords;
    private long _walRecoveryRecordsReplayed;
    private long _walRecoveryBytesReplayed;
    private bool _disposed;

    private LocalDiskStore(
        string root,
        FileStream lockStream,
        MidgeFileLease lease,
        FileStream walStream,
        MidgeManifest manifest,
        PantsRecoveryPolicy recoveryPolicy,
        PantsPerformanceGoal performanceGoal,
        IPantsFailpointHandler failpoints,
        PantsCompactionConfiguration compaction,
        long targetSstSizeBytes,
        PantsBlockCachePolicy blockCachePolicy,
        long blockCacheBytes)
    {
        _root = root;
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
        _walStream = walStream;
        _manifest = manifest;
        _nextSequence = manifest.LastPersistedSequence;
        _unflushedCommitSequence = manifest.LastPersistedSequence;
    }

    public int WalRecords => _walRecords;
    public long ActiveWalBytes => _walStream.Length;
    public string RootPath => _root;
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

    public long LastPersistedSequence => checked((long)_manifest.LastPersistedSequence);
    public long NextWalSequence => checked((long)_manifest.NextWalSeq);
    public ulong CurrentWalSegmentId => _manifest.NextWalSeq;
    public int SstCount => _manifest.Files.Count;
    public long SstBytes => checked((long)_manifest.Files.Aggregate(0UL, static (total, file) => total + file.SizeBytes));
    public long LocalWalBytes => checked(
        GetLocalFileBytes(_walDirectory, "*.wal") + GetExistingFileBytes(_walPath));
    public long LocalSstBytes => GetLocalFileBytes(_sstDirectory, "*.sst");
    public long LocalCommittedBytes => checked(LocalWalBytes + LocalSstBytes);
    public long WalRecoveryRecordsReplayed => _walRecoveryRecordsReplayed;
    public long WalRecoveryBytesReplayed => _walRecoveryBytesReplayed;
    public ulong WriterEpoch => _lease.Epoch;

    public PantsEngineHealth GetHealth(PantsRuntimeState state)
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
        var owned = _manifest.Files.Select(static file => file.Name).ToHashSet(StringComparer.Ordinal);
        return Directory
            .EnumerateFiles(_sstDirectory, "*.sst", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path))
            .Where(name => !owned.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public void CollectObsoleteFiles(PantsRuntimeState state)
    {
        if (state.ActiveSnapshotCount != 0)
        {
            return;
        }

        foreach (string name in _snapshotPinnedObsoleteFiles.ToArray())
        {
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
            _snapshotPinnedObsoleteFiles.Remove(name);
        }

        MidgeColumnFamilyMeta[] droppedFamilies = _manifest.ColumnFamilies
            .Where(static family => family.DeletedAt is not null && !family.Reclaimed)
            .ToArray();
        if (droppedFamilies.Length == 0)
        {
            return;
        }

        var edits = new List<JsonElement>();
        var obsoleteNames = new List<string>();
        foreach (MidgeColumnFamilyMeta family in droppedFamilies)
        {
            string[] names = _manifest.Files
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
        foreach (string name in obsoleteNames)
        {
            File.Delete(Path.Combine(_sstDirectory, name));
            RemoveSstFromCaches(name);
        }
    }

    public MidgeColumnFamilyMeta GetColumnFamilyMetadata(ColumnFamilyIdentity identity)
    {
        MidgeColumnFamilyMeta metadata = _manifest.ColumnFamilies.Single(family => family.Id == identity.Id);
        return metadata.Clone();
    }

    public IReadOnlyList<MidgeColumnFamilyMeta> GetColumnFamilyMetadataSnapshot() =>
        _manifest.ColumnFamilies.Select(static family => family.Clone()).ToArray();

    public IReadOnlyList<HybridLocalSst> GetLocalManifestSsts() =>
        _manifest.Files
            .OrderBy(static file => file.SstSequence)
            .ThenBy(static file => file.Name, StringComparer.Ordinal)
            .Where(file => File.Exists(Path.Combine(_sstDirectory, file.Name)))
            .Select(file => new HybridLocalSst(
                file.Name,
                new FileInfo(Path.Combine(_sstDirectory, file.Name)).Length))
            .ToArray();

    public IReadOnlyList<string> GetPointReadSstNames(
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key)
    {
        var keyCopy = key.ToArray();
        return _manifest.Files
            .Where(file =>
                file.ColumnFamilyId == columnFamily.Id &&
                IsWithinFileRange(file, keyCopy))
            .Select(static file => file.Name)
            .ToArray();
    }

    public IReadOnlyList<string> GetScanSstNames(
        ColumnFamilyIdentity columnFamily,
        PantsScanBounds bounds) =>
        _manifest.Files
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
        _manifest.Files.Select(static file => file.Name).ToArray();

    public bool IsSstLocal(string name)
    {
        _ = GetManifestSst(name);
        return File.Exists(Path.Combine(_sstDirectory, name));
    }

    public void HydrateLocalSst(string name, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        var metadata = GetManifestSst(name);
        if (checked((ulong)bytes.Length) != metadata.SizeBytes ||
            metadata.ContentCrc32C.HasValue &&
            MidgeDiskFormat.Crc32C(bytes) != metadata.ContentCrc32C.Value)
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{name}' does not match its manifest metadata.");
        }

        var bytesCopy = bytes.ToArray();
        _ = MidgeSstCodec.Decode(bytesCopy);
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

    public void EvictLocalSst(string name)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        _ = GetManifestSst(name);
        RemoveSstFromCaches(name);
        File.Delete(Path.Combine(_sstDirectory, name));
    }

    public bool RecordPointRead(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        ReadOnlySpan<byte> key)
    {
        byte[] keyCopy = key.ToArray();
        MidgeFileMeta[] familyFiles = _manifest.Files
            .Where(file => file.ColumnFamilyId == columnFamily.Id)
            .ToArray();
        MidgeFileMeta[] candidates = familyFiles
            .Where(file => IsWithinFileRange(file, keyCopy))
            .ToArray();
        int bloomChecks = 0;
        int candidateBlocks = 0;
        int amplificationBlocksRead = 0;
        int dataBlocksRead = 0;
        int bloomTruePositives = 0;
        int bloomFalsePositives = 0;
        int bloomTrueNegatives = 0;
        int blockCacheHits = 0;
        int blockCacheMisses = 0;
        int readerCacheHits = 0;
        int readerCacheMisses = 0;
        foreach (MidgeFileMeta candidate in candidates)
        {
            string path = Path.Combine(_sstDirectory, candidate.Name);
            MidgeSstReader reader = _readerCache.GetOrAdd(
                candidate.Name,
                path,
                out bool readerCacheHit);
            if (readerCacheHit)
            {
                readerCacheHits++;
            }
            else
            {
                readerCacheMisses++;
            }

            SstPointReadDecision decision = reader.GetPointReadDecision(keyCopy);
            bloomChecks = checked(bloomChecks + decision.BloomChecks);
            candidateBlocks = checked(candidateBlocks + decision.CandidateBlocks);
            bloomTrueNegatives = checked(bloomTrueNegatives + (decision.Rejected ? 1 : 0));
            amplificationBlocksRead = checked(
                amplificationBlocksRead + 1 + decision.BlocksRead);
            if (decision.BlocksRead == 0)
            {
                continue;
            }

            var cacheKey = new SstBlockCacheKey(candidate.Name, decision.CandidateBlockIndex);
            bool containsKey;
            if (_blockCache.TryGet(cacheKey, out SstBlockCacheEntry? cachedBlock) &&
                cachedBlock is not null)
            {
                blockCacheHits++;
                containsKey = cachedBlock.ContainsKey(keyCopy);
            }
            else
            {
                blockCacheMisses++;
                byte[] blockContent = reader.ReadDataBlock(decision.CandidateBlockIndex);
                dataBlocksRead = checked(dataBlocksRead + 1);
                containsKey = MidgeSstCodec.DataBlockContainsKey(blockContent, keyCopy);
                _ = _blockCache.Add(cacheKey, blockContent);
            }

            if (containsKey)
            {
                bloomTruePositives++;
            }
            else
            {
                bloomFalsePositives++;
            }
        }

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
            KeyRangeRejects = familyFiles.Length - candidates.Length,
            BloomChecks = bloomChecks,
            BloomTruePositives = bloomTruePositives,
            BloomFalsePositives = bloomFalsePositives,
            BloomTrueNegatives = bloomTrueNegatives
        });
    }

    public IScanReadValidator CreateScanReadValidator(
        RuntimeTelemetry telemetry,
        ColumnFamilyIdentity columnFamily,
        PantsScanBounds bounds)
    {
        var readers = new List<MidgeSstReader>();
        var blocks = new List<SstScanBlock>();
        try
        {
            foreach (MidgeFileMeta file in _manifest.Files.Where(file =>
                         file.ColumnFamilyId == columnFamily.Id &&
                         file.SmallestKey is not null &&
                         file.LargestKey is not null &&
                         bounds.Overlaps(GetMetadataKey(file.SmallestKey), GetMetadataKey(file.LargestKey))))
            {
                MidgeSstReader reader = MidgeSstReader.Open(Path.Combine(_sstDirectory, file.Name));
                readers.Add(reader);
                for (var blockIndex = 0; blockIndex < reader.DataBlockCount; blockIndex++)
                {
                    byte[] firstKey = reader.GetFirstKey(blockIndex);
                    byte[]? nextFirstKey = blockIndex + 1 < reader.DataBlockCount
                        ? reader.GetFirstKey(blockIndex + 1)
                        : null;
                    bool overlaps =
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

    private static byte[] GetMetadataKey(IReadOnlyList<int> key) =>
        key.Select(static value => checked((byte)value)).ToArray();

    public static LocalDiskStore Open(
        string directory,
        PantsRuntimeState state,
        ulong minimumWriterEpoch = 0,
        PantsRecoveryPolicy recoveryPolicy = PantsRecoveryPolicy.Strict,
        PantsPerformanceGoal performanceGoal = PantsPerformanceGoal.Latency,
        TimeSpan? leaseClockSkewTolerance = null,
        Action? leaseLossCallback = null,
        IPantsFailpointHandler? failpoints = null,
        PantsCompactionConfiguration? compaction = null,
        long targetSstSizeBytes = 128L * 1024 * 1024,
        PantsBlockCachePolicy blockCachePolicy = PantsBlockCachePolicy.Lru,
        long blockCacheBytes = 0,
        TimeSpan? leaseHeartbeatInterval = null,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? recoverySsts = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new PantsInvalidArgumentException("DataDirectory must not be empty.");
        }

        var root = Path.GetFullPath(directory);
        FileStream? lockStream = null;
        FileStream? walStream = null;
        MidgeFileLease? lease = null;
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

            lease = MidgeFileLease.Acquire(
                root,
                minimumWriterEpoch,
                leaseClockSkewTolerance ?? TimeSpan.FromSeconds(15),
                leaseLossCallback,
                leaseHeartbeatInterval ?? TimeSpan.FromSeconds(10));
            EnsureFormat(root);
            Directory.CreateDirectory(Path.Combine(root, "wal"));
            Directory.CreateDirectory(Path.Combine(root, "sst"));
            Directory.CreateDirectory(Path.Combine(root, "sst", ".flush-staging"));
            string intentPath = Path.Combine(root, "intent_log.json");
            if (!File.Exists(intentPath))
            {
                AtomicStagedFile.Write(intentPath, "[]"u8);
            }
            string journalPath = Path.Combine(root, "manifest.journal");
            if (!File.Exists(journalPath))
            {
                AtomicStagedFile.Write(journalPath, []);
            }
            foreach (var temporary in Directory.GetFiles(Path.Combine(root, "sst", ".flush-staging"), "*.tmp"))
            {
                File.Delete(temporary);
            }
            TransactionSpillStore.CleanupOrphans(root);
            MidgeManifest manifest = LoadManifest(root, recoveryPolicy, state);
            bool clearRecoveredIntents = ValidateRecoveryMetadata(
                root,
                manifest,
                recoveryPolicy,
                state);
            AdvanceNextWalSequencePastSealedSegments(
                Path.Combine(root, "wal"),
                manifest);
            walStream = new FileStream(Path.Combine(root, "wal", "wal.log"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var store = new LocalDiskStore(
                root,
                lockStream,
                lease,
                walStream,
                manifest,
                recoveryPolicy,
                performanceGoal,
                failpoints ?? NullPantsFailpointHandler.Instance,
                compaction ?? new PantsCompactionConfiguration(),
                targetSstSizeBytes,
                blockCachePolicy,
                blockCacheBytes);
            store.Recover(state, recoverySsts);
            store.SaveManifestCheckpoint();
            if (clearRecoveredIntents)
            {
                store.ClearIntentLog();
            }

            store._walStream.Seek(0, SeekOrigin.End);
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
            throw new PantsStorageException($"Could not open Pants database at '{root}'.", ex);
        }
    }

    static void AdvanceNextWalSequencePastSealedSegments(
        string walDirectory,
        MidgeManifest manifest)
    {
        var maximumSegmentId = Directory
            .EnumerateFiles(walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .Select(static name => ulong.TryParse(name, out var segmentId) ? segmentId : 0)
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
        _lease.EnsureValid();
        JsonElement edit = CreateManifestEdit(
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
        PantsRuntimeState state,
        ColumnFamilyIdentity identity)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        if (!_familyIds.TryGetValue(identity, out var id))
        {
            throw new PantsStorageException(
                $"Column family '{identity}' has no persistent Midge identity.");
        }

        var droppedSstNames = _manifest.Files
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

    public bool IsColumnFamilyEditApplied(JsonElement edit) =>
        CloudDdlEdit.Matches(_manifest.ColumnFamilies, edit);

    public void CommitColumnFamilyEdit(PantsRuntimeState state, JsonElement edit)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        CloudDdlEdit.Validate(edit);
        if (IsColumnFamilyEditApplied(edit))
        {
            ApplyColumnFamilyEditVisibility(state, edit);
            return;
        }

        _failpoints.Hit(PantsFailpoint.BeforeDdlLocalCommit);
        DurablyApplyManifestEdit(edit);
        _failpoints.Hit(PantsFailpoint.AfterDdlLocalJournalBeforeVisibility);
        ApplyColumnFamilyEditVisibility(state, edit);
        SaveManifestCheckpoint();
    }

    public void AdoptRemoteCommittedColumnFamilyEdit(
        PantsRuntimeState state,
        JsonElement edit)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        CloudDdlEdit.Validate(edit);
        if (!IsColumnFamilyEditApplied(edit))
        {
            DurablyApplyManifestEdit(edit);
        }

        ApplyColumnFamilyEditVisibility(state, edit);
    }

    public void ApplyColumnFamilyEditVisibility(PantsRuntimeState state, JsonElement edit)
    {
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
            state.FamilyData[identity] = new SortedDictionary<byte[], CellState>(
                ByteArrayComparer.Instance);
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

    public void DropColumnFamily(PantsRuntimeState state, ColumnFamilyIdentity identity)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        if (!_familyIds.TryGetValue(identity, out uint id))
        {
            throw new PantsStorageException($"Column family '{identity}' has no persistent Midge identity.");
        }

        string[] droppedSstNames = _manifest.Files
            .Where(file => file.ColumnFamilyId == id)
            .Select(file => file.Name)
            .ToArray();
        JsonElement edit = CreateManifestEdit(
            "DropColumnFamilyAt",
            new
            {
                id,
                drop_sequence = checked((ulong)state.Sequence),
                dropped_sst_names = droppedSstNames
            });
        DurablyApplyManifestEdit(edit);
        _familyIds.Remove(identity);
        SaveManifestCheckpoint();
    }

    public void AppendCommit(CommitPayload payload, PantsRuntimeState state, PantsDurability durability)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        if (payload.OrderedOperations.Count == 0)
        {
            return;
        }

        var beginSequence = _nextSequence;
        var mutations = payload.OrderedOperations.Select(operation => new MidgeWalMutation(
            ResolveFamilyId(operation.Family),
            operation.Kind switch
            {
                CommitOperationKind.Put => MidgeWalOperation.Put,
                CommitOperationKind.Delete => MidgeWalOperation.Delete,
                CommitOperationKind.DeleteRange => MidgeWalOperation.DeleteRange,
                _ => throw new PantsStorageException($"Unsupported WAL operation '{operation.Kind}'.")
            },
            operation.Key.ToArray(),
            operation.Value?.ToArray(),
            0,
            operation.ExpirationUnixMilliseconds,
            operation.EndExclusive?.ToArray())).ToList();
        for (var index = 0; index < mutations.Count; index++)
        {
            mutations[index] = mutations[index] with { Sequence = beginSequence + (ulong)index + 1 };
        }

        var commitSequence = beginSequence + (ulong)mutations.Count + 1;
        if (durability != PantsDurability.BestEffort)
        {
            _failpoints.Hit(PantsFailpoint.BeforeWalAppend);
            var encoded = MidgeWalCodec.EncodeTransactionBatch(checked((ulong)payload.TransactionId), beginSequence, _lease.Epoch, mutations);
            MidgeWalCodec.AppendFrame(
                _walStream.SafeFileHandle,
                _walStream.Length,
                encoded,
                () => _failpoints.Hit(PantsFailpoint.MidWalAppend));
            _failpoints.Hit(PantsFailpoint.AfterWalAppend);
            _failpoints.Hit(PantsFailpoint.BeforeWalFlush);
            _walRecords++;
            if (durability == PantsDurability.Sync)
            {
                _walStream.Flush(flushToDisk: true);
            }
            else
            {
                _walStream.Flush(flushToDisk: false);
            }

            _failpoints.Hit(PantsFailpoint.AfterWalFlush);
        }

        _mutableOperations.AddRange(mutations);
        _nextSequence = commitSequence;
        _unflushedCommitSequence = commitSequence;
        state.Sequence = checked((long)commitSequence);
    }

    public void Flush(PantsRuntimeState state)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        if (_mutableOperations.Count == 0)
        {
            SaveManifestCheckpoint();
            return;
        }

        FlushOperations(_mutableOperations, _unflushedCommitSequence);
        RotateWal();
        _mutableOperations.Clear();
    }

    public void Flush(PantsRuntimeState state, ColumnFamilyIdentity identity)
    {
        if (!_familyIds.TryGetValue(identity, out uint familyId))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is not active in persistent storage.");
        }

        List<MidgeWalMutation> familyOperations = _mutableOperations
            .Where(operation => operation.ColumnFamilyId == familyId)
            .ToList();
        if (familyOperations.Count == 0)
        {
            SaveManifestCheckpoint();
            return;
        }

        if (familyOperations.Count == _mutableOperations.Count)
        {
            Flush(state);
            return;
        }

        FlushOperations(familyOperations, persistedSequence: null);
        _mutableOperations.RemoveAll(operation => operation.ColumnFamilyId == familyId);
        _unflushedCommitSequence = checked(_mutableOperations.Max(static operation => operation.Sequence) + 1);
    }

    private void FlushOperations(
        IReadOnlyList<MidgeWalMutation> operations,
        ulong? persistedSequence)
    {
        var edits = new List<JsonElement>();
        var intents = new List<JsonElement>();
        foreach (IGrouping<uint, MidgeWalMutation> familyGroup in operations.GroupBy(
                     static operation => operation.ColumnFamilyId))
        {
            MidgeColumnFamilyMeta? familyMetadata = _manifest.ColumnFamilies.SingleOrDefault(
                family => family.Id == familyGroup.Key);
            if (familyMetadata?.DeletedAt is not null)
            {
                continue;
            }

            var entries = familyGroup
                .Where(operation => operation.Operation != MidgeWalOperation.DeleteRange)
                .Select(operation => new MidgeSstEntry(
                    operation.Key,
                    operation.Value,
                    operation.Sequence,
                    operation.Expiration,
                    operation.Operation == MidgeWalOperation.Delete))
                .ToList();
            var ranges = familyGroup
                .Where(operation => operation.Operation == MidgeWalOperation.DeleteRange && operation.RangeEnd is not null)
                .Select(operation => new MidgeRangeTombstone(operation.Key, operation.RangeEnd!, operation.Sequence))
                .ToList();
            MidgeFileMeta metadata = CreateSst(
                familyGroup.Key,
                0,
                entries,
                ranges,
                PantsFailpoint.AfterFlushOutputDurable);
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
                    sequence = metadata.LargestSequence ?? 0,
                    file_meta = CreateIntentFileMetadata(metadata)
                }));
        }

        SaveIntentLog(intents);
        _failpoints.Hit(PantsFailpoint.BeforeFlushManifestPublish);
        DurablyApplyManifestBatch(edits);
        _failpoints.Hit(PantsFailpoint.AfterFlushManifestPublish);
        if (persistedSequence.HasValue)
        {
            _manifest.LastPersistedSequence = Math.Max(
                _manifest.LastPersistedSequence,
                persistedSequence.Value);
        }

        SaveManifestCheckpoint();
        ClearIntentLog();
    }

    public void FlushDurabilityBoundary()
    {
        ThrowIfDisposed();
        _walStream.Flush(flushToDisk: true);
    }

    public SealedWalSegment? SealActiveWal()
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        if (_walStream.Length == 0)
        {
            return null;
        }

        _walStream.Flush(flushToDisk: true);
        _walStream.Dispose();
        _failpoints.Hit(PantsFailpoint.BeforeWalRotation);
        ulong segmentId = _manifest.NextWalSeq;
        string fileName = $"{segmentId:00000000000000000000}.wal";
        string sealedPath = Path.Combine(_walDirectory, fileName);
        try
        {
            File.Move(_walPath, sealedPath, overwrite: false);
            byte[] bytes = File.ReadAllBytes(sealedPath);
            _manifest.NextWalSeq = checked(segmentId + 1);
            SaveManifestCheckpoint();
            _walStream = new FileStream(
                _walPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read);
            _walRecords = 0;
            _failpoints.Hit(PantsFailpoint.AfterWalRotation);
            return new SealedWalSegment(
                segmentId,
                _lease.Epoch,
                _nextSequence,
                fileName,
                bytes);
        }
        catch
        {
            if (!File.Exists(_walPath) && File.Exists(sealedPath))
            {
                try
                {
                    File.Move(sealedPath, _walPath, overwrite: false);
                }
                catch (IOException)
                {
                    // The sealed segment remains immutable and recoverable. A
                    // fresh active segment is safer than appending to it.
                }
            }

            _walStream = new FileStream(
                _walPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            _walStream.Seek(0, SeekOrigin.End);
            throw;
        }
    }

    public IReadOnlyList<SealedWalSegment> GetSealedWalSegmentsForCloudPublication()
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        return Directory.EnumerateFiles(_walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(ReadSealedWalSegment)
            .ToArray();
    }

    public void DeleteCloudDurableWalSegment(SealedWalSegment segment)
    {
        ThrowIfDisposed();
        _lease.EnsureValid();
        var path = Path.Combine(_walDirectory, segment.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    static SealedWalSegment ReadSealedWalSegment(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!ulong.TryParse(Path.GetFileNameWithoutExtension(fileName), out var segmentId))
        {
            throw new PantsCorruptionException($"Sealed WAL name '{fileName}' is invalid.");
        }

        var bytes = File.ReadAllBytes(path);
        var cursor = 0;
        ulong maximumSequence = 0;
        ulong? writerEpoch = null;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 2 * sizeof(uint))
            {
                throw new PantsCorruptionException($"Sealed WAL '{fileName}' has a torn frame header.");
            }

            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(cursor, sizeof(uint))));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(cursor + sizeof(uint), sizeof(uint)));
            cursor += 2 * sizeof(uint);
            if (length > bytes.Length - cursor)
            {
                throw new PantsCorruptionException($"Sealed WAL '{fileName}' has a torn frame payload.");
            }

            var payload = bytes.AsSpan(cursor, length);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new PantsCorruptionException($"Sealed WAL '{fileName}' has a corrupt frame.");
            }

            _ = MidgeWalCodec.DecodeTransactionBatch(
                payload,
                out var commitSequence,
                out var frameWriterEpoch);
            if (writerEpoch.HasValue && writerEpoch.Value != frameWriterEpoch)
            {
                throw new PantsCorruptionException(
                    $"Sealed WAL '{fileName}' contains mixed writer epochs.");
            }

            writerEpoch = frameWriterEpoch;
            maximumSequence = Math.Max(maximumSequence, commitSequence);
            cursor += length;
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

    public PantsStorageLayout GetStorageLayout(PantsRuntimeState state)
    {
        PantsStorageLevelLayout[] levels = _manifest.Files
            .GroupBy(static file => file.Level)
            .OrderBy(static group => group.Key)
            .Select(group =>
            {
                PantsStorageFileLayout[] files = group
                    .OrderBy(static file => file.Name, StringComparer.Ordinal)
                    .Select(static file => new PantsStorageFileLayout(
                        file.Name,
                        checked((int)file.Level),
                        file.ColumnFamilyId,
                        checked((long)file.SizeBytes),
                        file.SmallestKey is null
                            ? null
                            : new ReadOnlyMemory<byte>(file.SmallestKey.Select(static value => checked((byte)value)).ToArray()),
                        file.LargestKey is null
                            ? null
                            : new ReadOnlyMemory<byte>(file.LargestKey.Select(static value => checked((byte)value)).ToArray()),
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
        DateTimeOffset now = state.Clock.UtcNow;
        PantsSnapshotPin[] snapshots = state.ActiveSnapshots
            .Select(snapshot => new PantsSnapshotPin(
                snapshot.SnapshotId,
                snapshot.BeginSequence,
                now <= snapshot.StartedAtUtc ? TimeSpan.Zero : now - snapshot.StartedAtUtc,
                1))
            .ToArray();
        return new PantsStorageLayout(
            GetHealth(state),
            checked((long)_manifest.LastPersistedSequence),
            checked((long)_manifest.NextWalSeq),
            levels,
            snapshots,
            0,
            [],
            GetObsoleteFiles());
    }

    public long Compact(
        PantsRuntimeState state,
        bool force,
        bool continueCompacting = false) =>
        CompactAsync(
                state,
                force,
                outputPublisher: null,
                continueCompacting,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public ValueTask<long> CompactAsync(
        PantsRuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        CancellationToken cancellationToken = default) =>
        CompactAsync(
            state,
            force,
            outputPublisher,
            continueCompacting: false,
            cancellationToken);

    async ValueTask<long> CompactAsync(
        PantsRuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool continueCompacting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _lease.EnsureValid();
        Flush(state);
        var obsoleteNames = new List<string>();
        var edits = new List<JsonElement>();
        var intents = new List<JsonElement>();
        var outputNames = new List<string>();
        long bytesRewritten = 0;
        foreach ((ColumnFamilyIdentity _, uint familyId) in _familyIds.ToList())
        {
            CompactionPlan? plan = LeveledCompactionPlanner.Pick(
                _manifest.Files,
                familyId,
                _compaction,
                state.ActiveSnapshots.Select(static snapshot => snapshot.BeginSequence).Cast<long?>().Min(),
                force);
            if (plan is null)
            {
                continue;
            }

            MidgeSstContents[] contents = plan.Inputs
                .Select(input => MidgeSstCodec.Decode(
                    PositionalFile.ReadAllBytes(Path.Combine(_sstDirectory, input.Name))))
                .ToArray();
            CompactionMergeResult merged = CompactionMerger.Merge(contents, plan);
            IReadOnlyList<CompactionMergeResult> partitions =
                CompactionOutputPartitioner.Partition(merged, _targetSstSizeBytes);
            var outputs = new List<MidgeFileMeta>(partitions.Count);
            ulong firstOutputSequence = _manifest.NextSstSeqs.TryGetValue(
                familyId,
                out ulong nextOutputSequence)
                ? nextOutputSequence
                : 1UL;
            for (var outputIndex = 0; outputIndex < partitions.Count; outputIndex++)
            {
                CompactionMergeResult partition = partitions[outputIndex];
                MidgeFileMeta output = CreateSst(
                    familyId,
                    plan.TargetLevel,
                    partition.Entries,
                    partition.RangeTombstones,
                    PantsFailpoint.AfterCompactionOutputDurable,
                    checked(firstOutputSequence + (ulong)outputIndex));
                outputs.Add(output);
                outputNames.Add(output.Name);
                edits.Add(CreateManifestEdit("AddSst", output));
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
            bytesRewritten = checked(bytesRewritten + plan.Inputs.Sum(
                static input => checked((long)input.SizeBytes)));
        }

        foreach (var family in _manifest.ColumnFamilies.Where(family => family.DeletedAt is not null && !family.Reclaimed))
        {
            var droppedFiles = _manifest.Files.Where(file => file.ColumnFamilyId == family.Id).ToList();
            obsoleteNames.AddRange(droppedFiles.Select(file => file.Name));
            edits.Add(CreateManifestEdit(
                "ReclaimColumnFamily",
                new
                {
                    id = family.Id,
                    names = droppedFiles.Select(static file => file.Name).ToArray()
                }));
        }

        SaveIntentLog(intents);
        if (outputPublisher is not null && outputNames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await outputPublisher(outputNames, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lease.EnsureValid();
        }

        _failpoints.Hit(PantsFailpoint.BeforeCompactionManifestPublish);
        DurablyApplyManifestBatch(edits);
        _failpoints.Hit(PantsFailpoint.AfterCompactionManifestPublish);
        SaveManifestCheckpoint();
        ClearIntentLog();
        foreach (string name in obsoleteNames)
        {
            if (state.ActiveSnapshotCount == 0)
            {
                File.Delete(Path.Combine(_sstDirectory, name));
                RemoveSstFromCaches(name);
            }
            else
            {
                _snapshotPinnedObsoleteFiles.Add(name);
            }
        }

        if ((force || continueCompacting) && bytesRewritten > 0)
        {
            bytesRewritten = checked(bytesRewritten + await CompactAsync(
                state,
                force: false,
                outputPublisher,
                continueCompacting: true,
                cancellationToken).ConfigureAwait(false));
        }

        return bytesRewritten;
    }

    private void RemoveSstFromCaches(string name)
    {
        _readerCache.RemoveFile(name);
        _blockCache.RemoveFile(name);
    }

    MidgeFileMeta GetManifestSst(string name)
    {
        var safeName = ValidateSstName(name);
        return _manifest.Files.SingleOrDefault(file =>
                StringComparer.Ordinal.Equals(file.Name, safeName)) ??
            throw new PantsCorruptionException(
                $"SST '{safeName}' is not owned by the active manifest.");
    }

    void Recover(
        PantsRuntimeState state,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? recoverySsts)
    {
        RestoreColumnFamilies(state);
        var recoveredOperations = new List<MidgeWalMutation>();
        foreach (MidgeFileMeta file in _manifest.Files.ToArray())
        {
            try
            {
                var name = ValidateSstName(file.Name);
                var path = Path.Combine(_sstDirectory, name);
                var bytes = File.Exists(path)
                    ? PositionalFile.ReadAllBytes(path)
                    : recoverySsts is not null && recoverySsts.TryGetValue(name, out var recovered)
                        ? recovered.ToArray()
                        : throw new PantsStorageException($"Manifest SST '{file.Name}' is missing.");
                if (file.ContentCrc32C.HasValue && MidgeDiskFormat.Crc32C(bytes) != file.ContentCrc32C.Value)
                {
                    throw new PantsStorageException($"Manifest SST '{file.Name}' content checksum mismatch.");
                }

                MidgeSstContents contents = MidgeSstCodec.Decode(bytes);
                recoveredOperations.AddRange(contents.Entries.Select(entry => new MidgeWalMutation(
                    file.ColumnFamilyId,
                    entry.IsDelete ? MidgeWalOperation.Delete : MidgeWalOperation.Put,
                    entry.Key,
                    entry.Value,
                    entry.Sequence,
                    entry.Expiration,
                    null)));
                recoveredOperations.AddRange(contents.RangeTombstones.Select(range => new MidgeWalMutation(
                    file.ColumnFamilyId,
                    MidgeWalOperation.DeleteRange,
                    range.Start,
                    null,
                    range.Sequence,
                    null,
                    range.End)));
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

        ApplyMutations(state, recoveredOperations.OrderBy(operation => operation.Sequence));
        ReplayWal(state);
        state.Sequence = checked((long)_nextSequence);
    }

    private void ReplayWal(PantsRuntimeState state)
    {
        string[] sealedSegments = Directory
            .EnumerateFiles(_walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
            .Where(static path => Path.GetFileName(path) != "wal.log")
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < sealedSegments.Length; index++)
        {
            string sealedSegment = sealedSegments[index];
            WalReplayOutcome outcome;
            using (var stream = new FileStream(
                       sealedSegment,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                outcome = ReplayWalStream(state, stream, allowIncompleteTail: false);
            }

            if (outcome == WalReplayOutcome.Salvaged)
            {
                RetainCorruptFile(sealedSegment);
                for (int laterIndex = index + 1; laterIndex < sealedSegments.Length; laterIndex++)
                {
                    RetainCorruptFile(sealedSegments[laterIndex]);
                }

                ResetActiveWalAfterSalvage();
                return;
            }
        }

        _walStream.Seek(0, SeekOrigin.Begin);
        if (ReplayWalStream(state, _walStream, allowIncompleteTail: true) == WalReplayOutcome.Salvaged)
        {
            ResetActiveWalAfterSalvage();
        }
    }

    private WalReplayOutcome ReplayWalStream(
        PantsRuntimeState state,
        FileStream stream,
        bool allowIncompleteTail)
    {
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            long recordStart = stream.Position;
            if (!MidgeDiskFormat.ReadExactly(stream, header))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            if (header.IndexOfAnyExcept((byte)0) < 0 && IsZeroFilledTail(stream))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                return HandleWalCorruption(
                    state,
                    "WAL record exceeds Midge's 64 MiB frame limit.");
            }

            byte[] payload = new byte[length];
            if (!MidgeDiskFormat.ReadExactly(stream, payload))
            {
                return HandleIncompleteWalTail(state, stream, recordStart, allowIncompleteTail);
            }

            if (MidgeDiskFormat.Crc32C(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[4..]))
            {
                return HandleWalCorruption(state, "WAL frame CRC32C mismatch.");
            }

            IReadOnlyList<MidgeWalMutation> mutations;
            ulong commitSequence;
            try
            {
                mutations = MidgeWalCodec.DecodeTransactionBatch(payload, out commitSequence);
            }
            catch (PantsException exception)
            {
                return HandleWalCorruption(state, "WAL transaction batch is corrupt.", exception);
            }

            _walRecoveryRecordsReplayed++;
            _walRecoveryBytesReplayed = checked(_walRecoveryBytesReplayed + payload.Length);
            _nextSequence = Math.Max(_nextSequence, commitSequence);
            if (commitSequence > _manifest.LastPersistedSequence)
            {
                Dictionary<uint, ulong> persistedFamilySequences = _manifest.Files
                    .Where(static file => file.LargestSequence.HasValue)
                    .GroupBy(static file => file.ColumnFamilyId)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.Max(file => file.LargestSequence!.Value));
                MidgeWalMutation[] unpersisted = mutations
                    .Where(mutation =>
                        mutation.Sequence > persistedFamilySequences.GetValueOrDefault(
                            mutation.ColumnFamilyId))
                    .ToArray();
                if (unpersisted.Length != 0)
                {
                    ApplyMutations(state, unpersisted);
                    RecordRecoveredMemtableBytes(state, unpersisted);
                    _mutableOperations.AddRange(unpersisted);
                    _unflushedCommitSequence = Math.Max(_unflushedCommitSequence, commitSequence);
                }
            }

            _walRecords++;
        }

        return WalReplayOutcome.Complete;
    }

    private void RecordRecoveredMemtableBytes(
        PantsRuntimeState state,
        IReadOnlyList<MidgeWalMutation> mutations)
    {
        Dictionary<uint, ColumnFamilyIdentity> identities = _familyIds.ToDictionary(
            static pair => pair.Value,
            static pair => pair.Key);
        foreach (IGrouping<uint, MidgeWalMutation> operations in mutations.GroupBy(
                     static mutation => mutation.ColumnFamilyId))
        {
            if (!identities.TryGetValue(operations.Key, out ColumnFamilyIdentity identity))
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

    private WalReplayOutcome HandleIncompleteWalTail(
        PantsRuntimeState state,
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

    private WalReplayOutcome HandleWalCorruption(
        PantsRuntimeState state,
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

    private static bool IsZeroFilledTail(FileStream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Position < stream.Length)
        {
            int requested = checked((int)Math.Min(buffer.Length, stream.Length - stream.Position));
            int read = stream.Read(buffer[..requested]);
            if (read == 0 || buffer[..read].IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ResetActiveWalAfterSalvage()
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

    private void RestoreColumnFamilies(PantsRuntimeState state)
    {
        state.FamilyGeneration.Clear();
        state.ActiveFamilyVersions.Clear();
        state.FamilyData.Clear();
        state.RangeTombstones.Clear();
        state.ActiveMemtableBytes.Clear();
        if (!_manifest.ColumnFamilies.Any(family => family.Id == 0 && family.Name == "default"))
        {
            var defaultIdentity = new ColumnFamilyIdentity(0, "default", PantsRuntimeState.DefaultFamilyVersion);
            state.FamilyGeneration["default"] = PantsRuntimeState.DefaultFamilyVersion;
            state.ActiveFamilyVersions["default"] = PantsRuntimeState.DefaultFamilyVersion;
            state.FamilyData[defaultIdentity] = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
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
                state.FamilyData[identity] = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
                state.RangeTombstones[identity] = [];
                state.ActiveMemtableBytes[identity] = 0;
                _familyIds[identity] = family.Id;
            }
        }

        if (!state.ActiveFamilyVersions.ContainsKey("default"))
        {
            throw new PantsStorageException("Midge manifest does not contain the active default column family.");
        }

        state.NextColumnFamilyId = _manifest.ColumnFamilies.Count == 0
            ? 1
            : checked(_manifest.ColumnFamilies.Max(family => family.Id) + 1);
    }

    private void ApplyMutations(PantsRuntimeState state, IEnumerable<MidgeWalMutation> mutations)
    {
        var identityById = _familyIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        foreach (var mutation in mutations)
        {
            if (!identityById.TryGetValue(mutation.ColumnFamilyId, out var identity) || !state.FamilyData.TryGetValue(identity, out var family))
            {
                continue;
            }

            switch (mutation.Operation)
            {
                case MidgeWalOperation.Put:
                case MidgeWalOperation.Insert:
                    family[mutation.Key] = CellState.FromUnixMilliseconds(
                        mutation.Value?.ToArray(),
                        checked((long)mutation.Sequence),
                        mutation.Expiration);
                    break;
                case MidgeWalOperation.Delete:
                    family.Remove(mutation.Key);
                    break;
                case MidgeWalOperation.DeleteRange when mutation.RangeEnd is not null:
                    state.RangeTombstones[identity].Add(new CommittedRangeTombstone(
                        mutation.Key.ToArray(),
                        mutation.RangeEnd.ToArray(),
                        checked((long)mutation.Sequence)));
                    foreach (var key in family.Keys.Where(key =>
                                 ByteArrayComparer.Instance.Compare(key, mutation.Key) >= 0 &&
                                 ByteArrayComparer.Instance.Compare(key, mutation.RangeEnd) < 0).ToList())
                    {
                        family.Remove(key);
                    }

                    break;
            }
        }
    }

    private MidgeFileMeta CreateSst(
        uint familyId,
        uint level,
        IReadOnlyList<MidgeSstEntry> entries,
        IReadOnlyList<MidgeRangeTombstone> ranges,
        PantsFailpoint outputDurableFailpoint,
        ulong? assignedSequence = null)
    {
        ulong sequence = assignedSequence ??
            (_manifest.NextSstSeqs.TryGetValue(familyId, out ulong nextSequence) ? nextSequence : 1UL);
        var name = $"{familyId:000000}_{level:00}_{sequence:00000000000000000000}.sst";
        var bytes = MidgeSstCodec.Encode(entries, ranges, _performanceGoal);
        var stagingPath = Path.Combine(_sstDirectory, ".flush-staging", name + ".tmp");
        var finalPath = Path.Combine(_sstDirectory, name);
        using (var stream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(finalPath))
        {
            byte[] existing = PositionalFile.ReadAllBytes(finalPath);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new PantsCorruptionException(
                    $"Unpublished SST residue '{name}' conflicts with the retry output.");
            }

            File.Delete(stagingPath);
        }
        else
        {
            File.Move(stagingPath, finalPath, overwrite: false);
        }

        _failpoints.Hit(outputDurableFailpoint);
        var allKeys = entries.Select(entry => entry.Key)
            .Concat(ranges.SelectMany(range => new[] { range.Start, range.End }))
            .OrderBy(key => key, ByteArrayComparer.Instance)
            .ToList();
        var allSequences = entries.Select(entry => entry.Sequence).Concat(ranges.Select(range => range.Sequence)).ToList();
        var metadata = new MidgeFileMeta
        {
            Name = name,
            Level = level,
            SizeBytes = checked((ulong)bytes.Length),
            ContentCrc32C = MidgeDiskFormat.Crc32C(bytes),
            ColumnFamilyId = familyId,
            SstSequence = sequence,
            SmallestKey = allKeys.Count == 0 ? null : allKeys[0].Select(value => (int)value).ToArray(),
            LargestKey = allKeys.Count == 0 ? null : allKeys[^1].Select(value => (int)value).ToArray(),
            SmallestSequence = allSequences.Count == 0 ? null : allSequences.Min(),
            LargestSequence = allSequences.Count == 0 ? null : allSequences.Max(),
            Sublevel = 0
        };
        return metadata;
    }

    private static Dictionary<string, object?> CreateIntentFileMetadata(MidgeFileMeta metadata) => new()
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

    private static JsonElement CreateIntentEntry(string variant, object value) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [variant] = value },
            JsonOptions);

    private void SaveIntentLog(List<JsonElement> intents)
    {
        AtomicStagedFile.Write(
            _intentPath,
            JsonSerializer.SerializeToUtf8Bytes(intents, JsonOptions),
            beforePublish: () => _failpoints.Hit(PantsFailpoint.BeforeIntentLogReplace));
        _failpoints.Hit(PantsFailpoint.AfterIntentLogReplace);
    }

    private void ClearIntentLog() => AtomicStagedFile.Write(_intentPath, "[]"u8);

    private static JsonElement CreateManifestEdit(string variant, object value) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [variant] = value },
            JsonOptions);

    private void DurablyApplyManifestBatch(List<JsonElement> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        DurablyApplyManifestEdit(CreateManifestEdit("Batch", edits));
    }

    private void DurablyApplyManifestEdit(JsonElement edit)
    {
        byte recordType = GetManifestEditRecordType(edit);
        ulong editId = checked(_manifest.EditCheckpointId + 1);
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

        byte[] record = EncodeManifestJournalRecord(recordType, payload);
        byte[] markerPayload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                last_persisted_sequence = editId,
                ts_millis = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            },
            JsonOptions);
        byte[] marker = EncodeManifestJournalRecord(9, markerPayload);
        _failpoints.Hit(PantsFailpoint.BeforeManifestJournalAppend);
        PositionalFile.AppendAndFlush(
            _manifestJournalPath,
            [record, marker],
            () => _failpoints.Hit(PantsFailpoint.AfterManifestJournalAppend),
            () => _failpoints.Hit(PantsFailpoint.BeforeManifestJournalSync),
            () => _failpoints.Hit(PantsFailpoint.AfterManifestJournalSync));

        ApplyManifestEdit(_manifest, edit, recordType);
        _manifest.EditCheckpointId = editId;
    }

    private static byte[] EncodeManifestJournalRecord(byte recordType, ReadOnlySpan<byte> payload)
    {
        byte[] record = GC.AllocateUninitializedArray<byte>(checked(1 + sizeof(uint) + payload.Length + sizeof(uint)));
        record[0] = recordType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), checked((uint)payload.Length));
        payload.CopyTo(record.AsSpan(1 + sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(1 + sizeof(uint) + payload.Length),
            Crc32(payload));
        return record;
    }

    private void SaveManifestCheckpoint()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_manifest, JsonOptions);
        AtomicStagedFile.Write(
            _manifestSnapshotPath,
            json,
            beforePublish: () => _failpoints.Hit(PantsFailpoint.BeforeManifestCheckpointReplace));
        AtomicStagedFile.Write(_manifestJournalPath, []);
        AtomicStagedFile.Write(_manifestPath, json);
        _failpoints.Hit(PantsFailpoint.AfterManifestCheckpointReplace);
    }

    private void RotateWal()
    {
        _failpoints.Hit(PantsFailpoint.BeforeWalRotation);
        _walStream.Dispose();
        using (var truncate = new FileStream(_walPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            truncate.Flush(flushToDisk: true);
        }

        _walStream = new FileStream(_walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _walRecords = 0;
        foreach (string sealedSegment in Directory.EnumerateFiles(
                     _walDirectory,
                     "*.wal",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(sealedSegment);
        }

        _failpoints.Hit(PantsFailpoint.AfterWalRotation);
    }

    private uint ResolveFamilyId(ColumnFamilyIdentity identity) =>
        _familyIds.TryGetValue(identity, out var id)
            ? id
            : throw new PantsStorageException($"Column family '{identity}' has no persistent Midge identity.");

    private static bool IsWithinFileRange(MidgeFileMeta file, ReadOnlySpan<byte> key)
    {
        if (file.SmallestKey is null || file.LargestKey is null)
        {
            return false;
        }

        byte[] smallest = file.SmallestKey.Select(static value => checked((byte)value)).ToArray();
        byte[] largest = file.LargestKey.Select(static value => checked((byte)value)).ToArray();
        return key.SequenceCompareTo(smallest) >= 0 && key.SequenceCompareTo(largest) <= 0;
    }

    private static void EnsureFormat(string root)
    {
        var path = Path.Combine(root, "FORMAT");
        const string expected = "midge-format-version=3\n";
        if (File.Exists(path))
        {
            if (File.ReadAllText(path) != expected)
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

        AtomicStagedFile.Write(path, System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private static MidgeManifest LoadManifest(
        string root,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        string snapshot = Path.Combine(root, "manifest.snapshot.json");
        string legacy = Path.Combine(root, "manifest.json");
        string[] sources = new[] { snapshot, legacy }.Where(File.Exists).ToArray();
        if (sources.Length == 0)
        {
            return MidgeManifest.CreateInitial();
        }

        Exception? failure = null;
        foreach (string source in sources)
        {
            try
            {
                MidgeManifest manifest = JsonSerializer.Deserialize<MidgeManifest>(
                    File.ReadAllBytes(source),
                    JsonOptions) ?? throw new JsonException("Midge manifest is empty.");
                if (failure is not null)
                {
                    state.MarkSalvageMode();
                }

                return manifest;
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

        return MidgeManifest.CreateInitial();
    }

    private static bool ValidateRecoveryMetadata(
        string root,
        MidgeManifest manifest,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        string journalPath = Path.Combine(root, "manifest.journal");
        try
        {
            ReplayManifestJournal(File.ReadAllBytes(journalPath), manifest);
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
        }

        string intentPath = Path.Combine(root, "intent_log.json");
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(intentPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The intent log root must be an array.");
            }

            return ReplayIntentLog(root, manifest, document.RootElement, recoveryPolicy, state);
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
            return false;
        }
    }

    private static bool ReplayIntentLog(
        string root,
        MidgeManifest manifest,
        JsonElement intentLog,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        int entryCount = intentLog.GetArrayLength();
        if (entryCount == 0)
        {
            return false;
        }

        state.IntentLogReplayRuns = checked(state.IntentLogReplayRuns + 1);
        state.IntentLogEntriesReplayed = checked(state.IntentLogEntriesReplayed + entryCount);
        bool hasEntries = false;
        bool safeToClear = true;
        foreach (JsonElement entry in intentLog.EnumerateArray())
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

            JsonProperty variant = entry.EnumerateObject().Single();
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
                    foreach (string name in GetStringArray(variant.Value, "input_files"))
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

    private static bool ReplayFlushIntent(
        string root,
        MidgeManifest manifest,
        JsonElement intent,
        string phase,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        JsonElement metadataElement = intent.TryGetProperty("file_meta", out JsonElement fileMetadata)
            ? fileMetadata
            : intent;
        MidgeFileMeta metadata = ParseIntentFileMetadata(metadataElement);
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

    private static bool ReplayCompactionIntent(
        string root,
        MidgeManifest manifest,
        JsonElement intent,
        string phase,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        string[] removed = GetStringArray(intent, "removed");
        if (removed.Length == 0)
        {
            return HandleIntentRecoveryIssue(
                "Compaction publication intent has no inputs.",
                recoveryPolicy,
                state);
        }

        foreach (string name in removed)
        {
            _ = ValidateSstName(name);
        }

        if (!intent.TryGetProperty("added", out JsonElement addedElement) ||
            addedElement.ValueKind != JsonValueKind.Array)
        {
            return HandleIntentRecoveryIssue(
                "Compaction publication intent has invalid outputs.",
                recoveryPolicy,
                state);
        }

        MidgeFileMeta[] added = addedElement
            .EnumerateArray()
            .Select(ParseIntentFileMetadata)
            .ToArray();
        var columnFamilyId = GetRequiredUInt32(intent, "cf_id");
        var columnFamilyInactive = manifest.ColumnFamilies.All(family =>
            family.Id != columnFamilyId || family.DeletedAt.HasValue);
        bool allInputsPresent = removed.All(name => manifest.Files.Any(file => file.Name == name));
        bool allInputsAbsent = removed.All(name => manifest.Files.All(file => file.Name != name));
        bool allOutputsPresent = added.All(output => manifest.Files.Any(file => file.Name == output.Name));
        bool allOutputsAbsent = added.All(output => manifest.Files.All(file => file.Name != output.Name));

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

        if ((phase is "OutputDurable" or "ManifestPublished") &&
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

    private static MidgeFileMeta ParseIntentFileMetadata(JsonElement element)
    {
        string name = ValidateSstName(GetRequiredString(element, "name"));
        return new MidgeFileMeta
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

    private static bool PublishRecoveredIntentSst(
        string root,
        MidgeManifest manifest,
        MidgeFileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        if (!ValidateIntentSst(root, metadata, recoveryPolicy, state))
        {
            return false;
        }

        manifest.Files.Add(metadata);
        return true;
    }

    private static bool DeleteUnpublishedIntentSst(
        string root,
        MidgeFileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        string path = Path.Combine(root, "sst", metadata.Name);
        if (!File.Exists(path))
        {
            return true;
        }

        return ValidateIntentSst(root, metadata, recoveryPolicy, state) &&
            DeleteRecoveredSst(root, metadata.Name, recoveryPolicy, state);
    }

    private static bool ValidateIntentSst(
        string root,
        MidgeFileMeta metadata,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        string path = Path.Combine(root, "sst", metadata.Name);
        try
        {
            byte[] bytes = PositionalFile.ReadAllBytes(path);
            if ((metadata.SizeBytes != 0 && metadata.SizeBytes != checked((ulong)bytes.Length)) ||
                (metadata.ContentCrc32C.HasValue &&
                 metadata.ContentCrc32C.Value != MidgeDiskFormat.Crc32C(bytes)))
            {
                return HandleIntentRecoveryIssue(
                    $"Recovery intent SST '{metadata.Name}' does not match its publication proof.",
                    recoveryPolicy,
                    state);
            }

            _ = MidgeSstCodec.Decode(bytes);
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

    private static bool DeleteRecoveredSst(
        string root,
        string name,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
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

    private static bool HandleIntentRecoveryIssue(
        string message,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state)
    {
        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            throw PantsException.Create(PantsErrorCode.RecoveryFailed, message);
        }

        state.MarkSalvageMode();
        return false;
    }

    private static void RecoverMetadataFile(
        string path,
        string strictMessage,
        byte[] replacement,
        PantsRecoveryPolicy recoveryPolicy,
        PantsRuntimeState state,
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

    internal static void ValidateManifestJournal(ReadOnlySpan<byte> bytes)
    {
        _ = ReadDurableJournalRecords(bytes);
    }

    private static void ReplayManifestJournal(ReadOnlySpan<byte> bytes, MidgeManifest manifest)
    {
        JournalRecord[] records = ReadDurableJournalRecords(bytes);
        ulong nextLegacyEditId = manifest.EditCheckpointId;
        foreach (JournalRecord record in records)
        {
            using JsonDocument document = JsonDocument.Parse(record.Payload);
            JsonElement edit = document.RootElement;
            ulong editId;
            if (edit.ValueKind == JsonValueKind.Object &&
                edit.TryGetProperty("edit_id", out JsonElement editIdElement) &&
                edit.TryGetProperty("edit", out JsonElement envelopedEdit))
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

    private static JournalRecord[] ReadDurableJournalRecords(ReadOnlySpan<byte> bytes)
    {
        int cursor = 0;
        var pending = new List<JournalRecord>();
        int durableCount = 0;
        while (cursor < bytes.Length)
        {
            int remaining = bytes.Length - cursor;
            if (remaining < 5)
            {
                break;
            }

            byte recordType = bytes[cursor];
            if (recordType is < 1 or > 11)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal contains an unknown record type.");
            }

            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 1)..]);
            if (payloadLength > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal record exceeds the recovery limit.");
            }

            int recordLength = checked(1 + sizeof(uint) + (int)payloadLength + sizeof(uint));
            if (recordLength > remaining)
            {
                break;
            }

            ReadOnlySpan<byte> payload = bytes.Slice(cursor + 5, checked((int)payloadLength));
            uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + 5 + checked((int)payloadLength), sizeof(uint)));
            if (Crc32(payload) != expectedChecksum)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "The manifest journal record checksum does not match.");
            }

            byte[] payloadCopy = payload.ToArray();
            try
            {
                using JsonDocument _ = JsonDocument.Parse(payloadCopy);
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
            }
            else
            {
                pending.Add(new JournalRecord(recordType, payloadCopy));
            }

            cursor += recordLength;
        }

        return pending.Take(durableCount).ToArray();
    }

    private static void ApplyManifestEdit(
        MidgeManifest manifest,
        JsonElement edit,
        byte recordType)
    {
        if (recordType == 8 && edit.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement nested in edit.EnumerateArray())
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

        JsonProperty variant = edit.EnumerateObject().Single();
        byte actualRecordType = GetManifestEditRecordType(variant.Name);
        if (actualRecordType != recordType)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "The manifest journal record type does not match its edit payload.");
        }

        JsonElement value = variant.Value;
        switch (variant.Name)
        {
            case "AddSst":
                {
                    MidgeFileMeta metadata = value.Deserialize<MidgeFileMeta>(JsonOptions) ??
                        throw PantsException.Create(PantsErrorCode.Corruption, "An AddSst edit is empty.");
                    manifest.Files.RemoveAll(file => file.Name == metadata.Name);
                    manifest.Files.Add(metadata);
                    break;
                }
            case "RemoveSst":
                manifest.Files.RemoveAll(file => file.Name == GetRequiredString(value, "name"));
                break;
            case "CreateColumnFamily":
                {
                    uint id = GetRequiredUInt32(value, "id");
                    string name = GetRequiredString(value, "name");
                    ulong createdAt = GetRequiredUInt64(value, "created_at");
                    MidgeColumnFamilyMeta? existing = manifest.ColumnFamilies.SingleOrDefault(family => family.Id == id);
                    if (existing is null)
                    {
                        manifest.ColumnFamilies.Add(new MidgeColumnFamilyMeta
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
                    GetStringArray(value, "dropped_sst_names"));
                break;
            case "ReclaimColumnFamily":
                {
                    uint id = GetRequiredUInt32(value, "id");
                    string[] names = GetStringArray(value, "names");
                    manifest.Files.RemoveAll(file => names.Contains(file.Name, StringComparer.Ordinal));
                    MidgeColumnFamilyMeta? family = manifest.ColumnFamilies.SingleOrDefault(candidate => candidate.Id == id);
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
                    uint id = GetRequiredUInt32(value, "cf_id");
                    ulong sequence = GetRequiredUInt64(value, "next_seq");
                    manifest.NextSstSeqs[id] = Math.Max(
                        manifest.NextSstSeqs.GetValueOrDefault(id),
                        sequence);
                    break;
                }
            case "SetCloudCheckpoint":
                manifest.CloudCheckpoint = JsonSerializer.Deserialize<object>(value.GetRawText(), JsonOptions);
                break;
            case "Batch":
                foreach (JsonElement nested in value.EnumerateArray())
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

    private static void ApplyDropColumnFamily(
        MidgeManifest manifest,
        uint id,
        ulong dropSequence,
        string[] droppedNames)
    {
        MidgeColumnFamilyMeta? family = manifest.ColumnFamilies.SingleOrDefault(candidate => candidate.Id == id);
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

    private static byte GetManifestEditRecordType(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count() != 1)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest edit is malformed.");
        }

        return GetManifestEditRecordType(edit.EnumerateObject().Single().Name);
    }

    private static byte GetManifestEditRecordType(string variant) => variant switch
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

    private static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    private static uint GetRequiredUInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint result)
            ? result
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    private static ulong GetRequiredUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt64(out ulong result)
            ? result
            : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    private static uint? GetOptionalUInt32(JsonElement element, string name) =>
        !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetUInt32(out uint result)
                ? result
                : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    private static ulong? GetOptionalUInt64(JsonElement element, string name) =>
        !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetUInt64(out ulong result)
                ? result
                : throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");

    private static int[]? GetOptionalByteArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");
        }

        return value.EnumerateArray().Select(static item => checked((int)item.GetByte())).ToArray();
    }

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, $"Manifest edit field '{name}' is invalid.");
        }

        return value.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb8_8320;
            }
        }

        return ~crc;
    }

    private static void RetainCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string retained = $"{path}.salvage-retained";
        for (int suffix = 1; File.Exists(retained); suffix++)
        {
            retained = $"{path}.salvage-retained.{suffix}";
        }

        File.Move(path, retained);
    }

    private static string ValidateSstName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name != Path.GetFileName(name) ||
            !name.EndsWith(".sst", StringComparison.Ordinal) ||
            name.Contains(':') ||
            name.Contains('\\'))
        {
            throw new PantsStorageException($"Manifest SST name '{name}' is unsafe.");
        }

        return name;
    }

    static long GetLocalFileBytes(string directory, string pattern)
    {
        var total = 0L;
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     pattern,
                     SearchOption.TopDirectoryOnly))
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readerCache.Dispose();
        _walStream.Dispose();
        _lease.Dispose();
        _lockStream.Dispose();
    }

    private enum WalReplayOutcome
    {
        Complete,
        ToleratedIncompleteTail,
        Salvaged
    }

    private sealed record JournalRecord(byte Type, byte[] Payload);
}
