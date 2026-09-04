namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionMerger
{
    public static CompactionMergeResult Merge(
        IEnumerable<SstContents> contents,
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
            .GroupBy(static entry => entry.Key, ByteArrayComparer.Instance)
            .SelectMany(group => RetainVersions(
                group.OrderByDescending(static entry => entry.Sequence),
                horizon,
                plan.PointTombstoneGcEligible,
                droppedRanges))
            .OrderBy(static entry => entry.Key, ByteArrayComparer.Instance)
            .ThenByDescending(static entry => entry.Sequence)
            .ToArray();

        return new CompactionMergeResult(entries, retainedRanges);
    }

    static List<SstEntry> RetainVersions(
        IEnumerable<SstEntry> orderedEntries,
        ulong? horizon,
        bool tombstoneGcEligible,
        IReadOnlyList<RangeTombstone> droppedRanges)
    {
        var versions = ValidateAndDeduplicate(orderedEntries)
            .Where(entry => !droppedRanges.Any(range => Covers(range, entry)))
            .ToArray();
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

    static IEnumerable<SstEntry> ValidateAndDeduplicate(IEnumerable<SstEntry> orderedEntries)
    {
        SstEntry? previous = null;
        foreach (var entry in orderedEntries)
        {
            if (previous is not null && previous.Sequence == entry.Sequence)
            {
                CompactionVersionIdentity.RequireMatchingContent(previous, entry);
                continue;
            }

            previous = entry;
            yield return entry;
        }
    }

    static bool CanDrop(ulong sequence, ulong? horizon, bool eligible) =>
        eligible && (horizon is null || sequence <= horizon.Value);

    static bool Covers(RangeTombstone range, SstEntry entry) =>
        entry.Sequence < range.Sequence &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.Start) >= 0 &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.End) < 0;
}
