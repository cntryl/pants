namespace Pants;

public sealed record PantsStorageLayout(
    PantsEngineHealth Health,
    long ManifestLastPersistedSequence,
    long ManifestNextWalSequence,
    IReadOnlyList<PantsStorageLevelLayout> Levels,
    IReadOnlyList<PantsSnapshotPin> ActiveSnapshots,
    int PendingCompactions,
    IReadOnlyList<string> CompactingSsts,
    IReadOnlyList<string> ObsoleteFiles);
