using System.Collections.Immutable;

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

    public uint NextColumnFamilyId { get; set; } = 1;

    public Dictionary<string, int> FamilyGeneration { get; }

    public Dictionary<string, int> ActiveFamilyVersions { get; }

    public Dictionary<ColumnFamilyIdentity, ImmutableSortedDictionary<byte[], CellState>> FamilyData { get; }

    public Dictionary<ColumnFamilyIdentity, ImmutableArray<CommittedRangeTombstone>> RangeTombstones { get; }

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

    public DatabaseVersion CreateVersion() => new(
        Sequence,
        FamilyData.ToImmutableDictionary(ColumnFamilyIdentityComparer.Instance),
        RangeTombstones.ToImmutableDictionary(ColumnFamilyIdentityComparer.Instance),
        ActiveFamilyVersions.ToImmutableDictionary(StringComparer.Ordinal));

    static TaskCompletionSource CreateWritePressureCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
