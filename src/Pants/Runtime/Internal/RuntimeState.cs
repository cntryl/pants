using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeState
{
    public const int DefaultFamilyVersion = 0;
    public static ImmutableSortedDictionary<byte[], CellState> EmptyFamily { get; } =
        ImmutableSortedDictionary.Create<byte[], CellState>(ByteArrayComparer.Instance);
    readonly RuntimeTelemetry _telemetry;
    TaskCompletionSource _writePressureChanged = CreateWritePressureCompletion();

    public RuntimeState(IPantsClock clock, RuntimeTelemetry telemetry)
    {
        Clock = clock;
        _telemetry = telemetry;
        FamilyGeneration = new Dictionary<string, int>(StringComparer.Ordinal);
        ActiveFamilyVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        FamilyData = new Dictionary<ColumnFamilyIdentity, ImmutableSortedDictionary<byte[], CellState>>(
            ColumnFamilyIdentityComparer.Instance);
        RangeTombstones = new Dictionary<ColumnFamilyIdentity, ImmutableArray<CommittedRangeTombstone>>(
            ColumnFamilyIdentityComparer.Instance);
        ActiveMemtableBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        ActiveTransactions = [];
        DirectReadOnlyTransactions = [];
        ActiveScanSnapshots = [];
        ImmutableMemtableFlushes = [];
        ActiveFamilyVersions["default"] = DefaultFamilyVersion;
        FamilyGeneration["default"] = DefaultFamilyVersion;
        var defaultFamily = new ColumnFamilyIdentity(0, "default", DefaultFamilyVersion);
        FamilyData[defaultFamily] = EmptyFamily;
        RangeTombstones[defaultFamily] = [];
        ActiveMemtableBytes[defaultFamily] = 0;
    }

    public IPantsClock Clock { get; }

    public long Sequence { get; set; }

    public long TransactionCounter { get; set; }

    public long DirectReadOnlyTransactionCounter;

    public uint NextColumnFamilyId { get; set; } = 1;

    public Dictionary<string, int> FamilyGeneration { get; }

    public Dictionary<string, int> ActiveFamilyVersions { get; }

    public Dictionary<ColumnFamilyIdentity, ImmutableSortedDictionary<byte[], CellState>> FamilyData { get; }

    public Dictionary<ColumnFamilyIdentity, ImmutableArray<CommittedRangeTombstone>> RangeTombstones { get; }

    public Dictionary<ColumnFamilyIdentity, long> ActiveMemtableBytes { get; }

    public Dictionary<long, TransactionInfo> ActiveTransactions { get; }

    public ConcurrentDictionary<long, TransactionInfo> DirectReadOnlyTransactions { get; }

    public Dictionary<long, ScanSnapshotPin> ActiveScanSnapshots { get; }

    public Dictionary<long, ImmutableMemtableFlush> ImmutableMemtableFlushes { get; }

    public int ActiveSnapshotCount =>
        ActiveTransactions.Count + DirectReadOnlyTransactions.Count + ActiveScanSnapshots.Count;

    public IEnumerable<ISnapshotPin> ActiveSnapshots =>
        ActiveTransactions.Values.Cast<ISnapshotPin>()
            .Concat(DirectReadOnlyTransactions.Values)
            .Concat(ActiveScanSnapshots.Values);

    public Task WritePressureChanged => _writePressureChanged.Task;

    public HashSet<ColumnFamilyIdentity> UnflushedFamilies { get; } =
        new(ColumnFamilyIdentityComparer.Instance);

    public PantsEngineHealth Health { get; set; } = PantsEngineHealth.Healthy;

    public bool IsShuttingDown { get; set; }

    public void SignalWritePressureChanged()
    {
        var completed = _writePressureChanged;
        _writePressureChanged = CreateWritePressureCompletion();
        completed.TrySetResult();
    }

    public void MarkSalvageMode()
    {
        if (Health != PantsEngineHealth.SalvageMode)
        {
            _telemetry.RecordSalvageModeOpen();
        }

        Health = PantsEngineHealth.SalvageMode;
    }

    public void RecordNoSpaceEvent() => _telemetry.RecordNoSpaceEvent();

    public void RecordWalRecovery(int payloadBytes) =>
        _telemetry.RecordWalRecovery(payloadBytes);

    public void RecordIntentLogReplay(int entryCount) =>
        _telemetry.RecordIntentLogReplay(entryCount);

    /// <summary>
    /// Removes exactly the keys a successfully-published flush covered from <see cref="FamilyData"/>,
    /// once that flush's SST is durable. Point reads for those keys fall through to the SST instead
    /// (see <c>SnapshotReadPath</c>). Safe to call even while other snapshots are active: because
    /// <see cref="FamilyData"/>'s roots are structurally-shared immutable trees, an already-open
    /// snapshot's own <see cref="DatabaseVersion.Families"/> copy was captured before this call and
    /// is untouched by it — only <em>new</em> snapshots stop seeing these keys in memory, and their
    /// pinned <see cref="DatabaseVersion.VisibleFiles"/> already includes the newly-published SST.
    /// A key whose <see cref="CellState.WriteSequence"/> no longer matches what the flush covered
    /// (i.e. it was overwritten after the flush was taken) is left in place.
    /// </summary>
    public void ReleaseFlushedGeneration(FrozenMemtableFlush flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        if (!FamilyData.TryGetValue(flush.ColumnFamily, out var current))
        {
            return;
        }

        var frontier = checked((long)flush.FrontierSequence);
        var updated = current;
        foreach (var operation in flush.Operations)
        {
            // <= rather than an exact sequence match: what matters is only that the
            // currently-resident value was captured at or before this flush's frontier (so this
            // SST durably covers it), not that it came from this exact WalMutation — WAL and
            // in-memory commit-sequence numbering are not required to line up one-to-one.
            if (updated.TryGetValue(operation.Key, out var cell) && cell.WriteSequence <= frontier)
            {
                updated = updated.Remove(operation.Key);
            }

            if (operation.Operation == WalOperation.DeleteRange && operation.RangeEnd is not null)
            {
                foreach (var key in updated
                             .Where(pair =>
                                 pair.Value.WriteSequence <= frontier &&
                                 ByteArrayComparer.Instance.Compare(pair.Key, operation.Key) >= 0 &&
                                 ByteArrayComparer.Instance.Compare(pair.Key, operation.RangeEnd) < 0)
                             .Select(static pair => pair.Key)
                             .ToArray())
                {
                    updated = updated.Remove(key);
                }
            }
        }

        if (!ReferenceEquals(updated, current))
        {
            FamilyData[flush.ColumnFamily] = updated;
        }

        if (RangeTombstones.TryGetValue(flush.ColumnFamily, out var tombstones))
        {
            RangeTombstones[flush.ColumnFamily] = tombstones
                .Where(tombstone => tombstone.WriteSequence > frontier)
                .ToImmutableArray();
        }
    }

    /// <summary>
    /// Releases the current generation for a column family after the serialized cloud flush
    /// path has published all of that family's mutable operations. Existing snapshots retain
    /// their immutable roots; snapshots created after publication fall through to the SST.
    /// </summary>
    public void ReleasePersistedFamily(ColumnFamilyIdentity identity)
    {
        if (FamilyData.ContainsKey(identity))
        {
            FamilyData[identity] = EmptyFamily;
        }

        if (RangeTombstones.ContainsKey(identity))
        {
            RangeTombstones[identity] = [];
        }
    }

    static readonly ImmutableDictionary<uint, ImmutableArray<FileMeta>> EmptyVisibleFiles =
        ImmutableDictionary<uint, ImmutableArray<FileMeta>>.Empty;

    public DatabaseVersion CreateVersion() => CreateVersion(EmptyVisibleFiles);

    /// <summary>
    /// Builds an immutable read snapshot. <paramref name="visibleFiles"/> is the manifest's
    /// published SST files at this moment, grouped by column-family id (see
    /// <see cref="Storage.Internal.LocalDiskStore.GetVisibleFilesSnapshot"/>) — pinning this
    /// list on the snapshot, rather than reading the live manifest later, is what lets a
    /// concurrent compaction/flush publish without changing what an already-open snapshot sees.
    /// </summary>
    public DatabaseVersion CreateVersion(
        IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> visibleFiles) => new(
        Sequence,
        FamilyData.ToImmutableDictionary(ColumnFamilyIdentityComparer.Instance),
        RangeTombstones.ToImmutableDictionary(ColumnFamilyIdentityComparer.Instance),
        ActiveFamilyVersions.ToImmutableDictionary(StringComparer.Ordinal),
        visibleFiles.ToImmutableDictionary());

    static TaskCompletionSource CreateWritePressureCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
