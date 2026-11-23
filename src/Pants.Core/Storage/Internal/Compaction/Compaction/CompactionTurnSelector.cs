namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     How aggressively a compaction turn rewrites levels that are not yet over their targets.
/// </summary>
enum CompactionTurnMode
{
    /// <summary>Only work that is over a trigger or a size target.</summary>
    Ordinary,

    /// <summary>Also rewrites L0 and inner levels that hold more than one file.</summary>
    Force,

    /// <summary>Drains every L0 file and overfull inner level, never rewriting under-target levels.</summary>
    DrainDebt
}

/// <summary>
///     Chooses the single plan of one compaction turn across every column family. A turn runs one
///     plan for one family, so per-turn work is bounded by that plan however many families exist.
///     Priority order: structural repair, critical L0 (round-robin), the deepest overfull inner
///     level across all families, then soft L0 (round-robin).
/// </summary>
static class CompactionTurnSelector
{
    public static CompactionPlan? Select(
        IReadOnlyList<FileMeta> files,
        IEnumerable<uint> familyIds,
        IReadOnlySet<uint> criticalL0Families,
        uint? lastScheduledFamilyId,
        PantsCompactionConfiguration configuration,
        ulong? snapshotHorizon,
        long? preferredWorkBytes,
        CompactionTurnMode mode,
        IReadOnlySet<uint>? excludedFamilies = null
    )
    {
        var floor = mode == CompactionTurnMode.Force ? 2 : int.MaxValue;
        var l0Floor = mode == CompactionTurnMode.DrainDebt ? 1 : floor;
        var rotation = CompactionFamilyRoundRobin.OrderFamilies(
            excludedFamilies is null ? familyIds : familyIds.Where(id => !excludedFamilies.Contains(id)),
            lastScheduledFamilyId);

        foreach (var familyId in rotation)
        {
            var repair = LeveledCompactionPlanner.PickRepair(files, familyId, configuration);

            if (repair is not null)
            {
                return repair;
            }
        }

        foreach (var familyId in rotation.Where(criticalL0Families.Contains))
        {
            var plan = LeveledCompactionPlanner.PickCriticalL0(
                files, familyId, configuration, snapshotHorizon, preferredWorkBytes);

            if (plan is not null)
            {
                return plan;
            }
        }

        CompactionPlan? deepest = null;

        foreach (var familyId in rotation)
        {
            var plan = LeveledCompactionPlanner.PickInnerLevel(
                files, familyId, configuration, snapshotHorizon, floor, preferredWorkBytes);

            if (plan is not null && (deepest is null || plan.SourceLevel > deepest.SourceLevel))
            {
                deepest = plan;
            }
        }


        if (deepest is not null)
        {
            return deepest;
        }

        foreach (var familyId in rotation)
        {
            var plan = LeveledCompactionPlanner.PickSoftL0(
                files, familyId, configuration, snapshotHorizon, l0Floor);

            if (plan is not null)
            {
                return plan;
            }
        }

        return null;
    }
}
