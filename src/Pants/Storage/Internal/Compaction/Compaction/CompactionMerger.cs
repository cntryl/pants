namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionMerger
{
    public static CompactionMergeResult Merge(
        IEnumerable<MidgeSstContents> contents,
        CompactionPlan plan)
    {
        ulong? horizon = plan.SnapshotHorizon is { } value ? checked((ulong)value) : null;
        var allRanges = contents
            .SelectMany(static content => content.RangeTombstones)
            .OrderBy(static tombstone => tombstone.Start, ByteArrayComparer.Instance)
            .ThenByDescending(static tombstone => tombstone.Sequence)
            .ToArray();
        var retainedRanges = allRanges
            .Where(tombstone => !CanDrop(tombstone.Sequence, horizon, plan.RangeTombstoneGcEligible))
            .ToArray();
        var droppedRanges = allRanges
            .Where(tombstone => CanDrop(tombstone.Sequence, horizon, plan.RangeTombstoneGcEligible))
            .ToArray();

        var entries = contents
            .SelectMany(static content => content.Entries)
            .Where(entry => !droppedRanges.Any(range => Covers(range, entry)))
            .GroupBy(static entry => entry.Key, ByteArrayComparer.Instance)
            .SelectMany(group => RetainVersions(
                group.OrderByDescending(static entry => entry.Sequence),
                horizon,
                plan.PointTombstoneGcEligible))
            .OrderBy(static entry => entry.Key, ByteArrayComparer.Instance)
            .ThenByDescending(static entry => entry.Sequence)
            .ToArray();

        return new CompactionMergeResult(entries, retainedRanges);
    }

    static List<MidgeSstEntry> RetainVersions(
        IEnumerable<MidgeSstEntry> orderedEntries,
        ulong? horizon,
        bool tombstoneGcEligible)
    {
        var versions = orderedEntries.ToArray();
        if (versions.Length == 0)
        {
            return [];
        }

        var newest = versions[0];
        if (newest.IsDelete && CanDrop(newest.Sequence, horizon, tombstoneGcEligible))
        {
            return [];
        }

        if (horizon is null)
        {
            return [newest];
        }

        var retained = versions.TakeWhile(entry => entry.Sequence > horizon.Value).ToList();
        var visibleAtHorizon = versions.FirstOrDefault(entry => entry.Sequence <= horizon.Value);
        if (visibleAtHorizon is not null)
        {
            retained.Add(visibleAtHorizon);
        }

        return retained;
    }

    static bool CanDrop(ulong sequence, ulong? horizon, bool eligible) =>
        eligible && (horizon is null || sequence <= horizon.Value);

    static bool Covers(MidgeRangeTombstone range, MidgeSstEntry entry) =>
        entry.Sequence < range.Sequence &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.Start) >= 0 &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.End) < 0;
}
