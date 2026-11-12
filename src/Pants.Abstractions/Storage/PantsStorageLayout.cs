namespace Cntryl.Pants.Storage;

public sealed record PantsStorageLayout(
    PantsEngineHealth Health,
    ulong ManifestLastPersistedSequence,
    ulong ManifestNextWalSequence,
    IReadOnlyList<PantsStorageLevelLayout> Levels,
    IReadOnlyList<PantsSnapshotPin> ActiveSnapshots,
    int PendingCompactions,
    IReadOnlyList<string> CompactingSsts,
    IReadOnlyList<string> ObsoleteFiles);
