namespace Pants;

internal sealed class PantsRuntimeState
{
    TaskCompletionSource _writePressureChanged = CreateWritePressureCompletion();

    public PantsRuntimeState(IPantsClock clock)
    {
        Clock = clock;
        FamilyGeneration = new Dictionary<string, int>(StringComparer.Ordinal);
        ActiveFamilyVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        FamilyData = new Dictionary<ColumnFamilyIdentity, SortedDictionary<byte[], CellState>>(
            ColumnFamilyIdentityComparer.Instance);
        RangeTombstones = new Dictionary<ColumnFamilyIdentity, List<CommittedRangeTombstone>>(
            ColumnFamilyIdentityComparer.Instance);
        ActiveMemtableBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        ActiveTransactions = [];
        ActiveScanSnapshots = [];
        ImmutableMemtableFlushes = [];
        ActiveFamilyVersions["default"] = DefaultFamilyVersion;
        FamilyGeneration["default"] = DefaultFamilyVersion;
        var defaultFamily = new ColumnFamilyIdentity(0, "default", DefaultFamilyVersion);
        FamilyData[defaultFamily] = new SortedDictionary<byte[], CellState>(ByteArrayComparer.Instance);
        RangeTombstones[defaultFamily] = [];
        ActiveMemtableBytes[defaultFamily] = 0;
    }

    public const int DefaultFamilyVersion = 0;

    public IPantsClock Clock { get; }

    public long Sequence { get; set; }

    public long TransactionCounter { get; set; }

    public uint NextColumnFamilyId { get; set; } = 1;

    public Dictionary<string, int> FamilyGeneration { get; }

    public Dictionary<string, int> ActiveFamilyVersions { get; }

    public Dictionary<ColumnFamilyIdentity, SortedDictionary<byte[], CellState>> FamilyData { get; }

    public Dictionary<ColumnFamilyIdentity, List<CommittedRangeTombstone>> RangeTombstones { get; }

    public Dictionary<ColumnFamilyIdentity, long> ActiveMemtableBytes { get; }

    public Dictionary<long, TransactionInfo> ActiveTransactions { get; }

    public Dictionary<long, ScanSnapshotPin> ActiveScanSnapshots { get; }

    public Dictionary<long, ImmutableMemtableFlush> ImmutableMemtableFlushes { get; }

    public int ActiveSnapshotCount => ActiveTransactions.Count + ActiveScanSnapshots.Count;

    public IEnumerable<ISnapshotPin> ActiveSnapshots =>
        ActiveTransactions.Values.Cast<ISnapshotPin>().Concat(ActiveScanSnapshots.Values);

    public Task WritePressureChanged => _writePressureChanged.Task;

    public HashSet<ColumnFamilyIdentity> UnflushedFamilies { get; } =
        new(ColumnFamilyIdentityComparer.Instance);

    public PantsEngineHealth Health { get; set; } = PantsEngineHealth.Healthy;

    public long SalvageModeOpens { get; set; }

    public long IntentLogReplayRuns { get; set; }

    public long IntentLogEntriesReplayed { get; set; }

    public long NoSpaceEvents { get; set; }

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
            SalvageModeOpens++;
        }

        Health = PantsEngineHealth.SalvageMode;
    }

    public DatabaseSnapshot CreateSnapshot()
    {
        var familyDataSnapshot =
            new Dictionary<ColumnFamilyIdentity, SortedDictionary<byte[], CellState>>(
                ColumnFamilyIdentityComparer.Instance);
        foreach ((ColumnFamilyIdentity family, SortedDictionary<byte[], CellState> data) in FamilyData)
        {
            familyDataSnapshot[family] = new SortedDictionary<byte[], CellState>(
                data,
                ByteArrayComparer.Instance);
        }

        var familyVersionsSnapshot = new Dictionary<string, int>(
            ActiveFamilyVersions,
            StringComparer.Ordinal);
        return new DatabaseSnapshot(Sequence, familyDataSnapshot, familyVersionsSnapshot);
    }

    static TaskCompletionSource CreateWritePressureCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
