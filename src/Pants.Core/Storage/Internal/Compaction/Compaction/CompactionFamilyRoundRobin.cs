namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionFamilyRoundRobin
{
    public static uint[] OrderFamilies(IEnumerable<uint> familyIds, uint? lastScheduledFamilyId)
    {
        var orderedFamilyIds = familyIds
            .Distinct()
            .Order()
            .ToArray();
        if (orderedFamilyIds.Length == 0)
        {
            return [];
        }

        var nextFamilyIndex = lastScheduledFamilyId is { } lastFamilyId
            ? Array.FindIndex(orderedFamilyIds, familyId => familyId > lastFamilyId)
            : 0;
        if (nextFamilyIndex < 0)
        {
            nextFamilyIndex = 0;
        }

        return orderedFamilyIds
            .Skip(nextFamilyIndex)
            .Concat(orderedFamilyIds.Take(nextFamilyIndex))
            .ToArray();
    }

    public static uint[] OrderCriticalFamilies(
        IEnumerable<uint> familyIds,
        IReadOnlySet<uint> criticalFamilies,
        uint? lastScheduledFamilyId) =>
        OrderFamilies(familyIds, lastScheduledFamilyId)
            .Where(criticalFamilies.Contains)
            .ToArray();
}
