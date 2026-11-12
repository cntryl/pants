namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>Admits whole validated replay units using durable family authority.</summary>
static class WalReplaySelection
{
    public static ulong RequireFamilyFrontier(IReadOnlyDictionary<uint, ulong> frontiers, uint familyId) =>
        frontiers.TryGetValue(familyId, out var frontier)
            ? frontier
            : throw new PantsCorruptionException($"WAL family {familyId} has no persisted flush authority.");

    public static bool ShouldApply(IReadOnlyDictionary<uint, ulong> frontiers, uint familyId, ulong commitSequence) =>
        commitSequence > RequireFamilyFrontier(frontiers, familyId);
}
