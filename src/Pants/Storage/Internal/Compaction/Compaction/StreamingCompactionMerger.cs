namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
/// Incremental counterpart to <c>CompactionMerger.Merge</c> + <c>CompactionOutputPartitioner.Partition</c>:
/// does the same k-way merge across inputs, the same version-retention/tombstone-covers-entry/
/// GC-eligibility filtering, and the same size-based output partitioning with per-partition
/// range-tombstone clamping — but drives it entry-by-entry over one <see cref="SstBlockIterator"/>
/// per input instead of materializing every input's decoded entries into one array first and
/// then slicing it. Range tombstones remain a small, resident-per-file list (loaded eagerly by
/// <see cref="SstReader.Open"/>, same as elsewhere in this codebase) — only the entry stream is
/// genuinely lazy. The retention/masking rules themselves are intentionally copied verbatim from
/// <c>CompactionMerger</c>/<c>CompactionOutputPartitioner</c> rather than shared, so a change to
/// one is a visible diff against the other, not a silent behavioral drift; keep them in sync
/// (and keep <see cref="Storage.Internal.Compaction.Compaction.StreamingCompactionMergerTests"/>
/// — or wherever the equivalence suite lives — green) if either changes.
/// </summary>
static class StreamingCompactionMerger
{
    const int EntryOverheadBytes = 32;

    public static IReadOnlyList<CompactionMergeResult> MergeAndPartition(
        IReadOnlyList<SstReader> readers,
        CompactionPlan plan,
        long targetSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSizeBytes);
        var horizon = plan.SnapshotHorizon is { } value ? checked((ulong)value) : (ulong?)null;
        var allRanges = readers
            .SelectMany(static reader => reader.RangeTombstones)
            .OrderBy(static tombstone => tombstone.Start, ByteArrayComparer.Instance)
            .ThenByDescending(static tombstone => tombstone.Sequence)
            .ToArray();
        var droppedRanges = allRanges
            .Where(tombstone => CanDrop(tombstone.Sequence, horizon, plan.RangeTombstoneGcEligible))
            .ToArray();
        var retainedRanges = allRanges
            .Where(tombstone => !CanDrop(tombstone.Sequence, horizon, plan.RangeTombstoneGcEligible))
            .ToArray();

        if (readers.Count == 0)
        {
            return retainedRanges.Length == 0
                ? []
                : CompactionOutputPartitioner.Partition(
                    new CompactionMergeResult([], retainedRanges),
                    targetSizeBytes);
        }

        var partitions = new List<CompactionMergeResult>();
        var currentEntries = new List<SstEntry>();
        long currentBytes = 0;
        byte[]? regionStart = null;

        foreach (var entry in MergeEntries(readers, horizon, plan.PointTombstoneGcEligible, droppedRanges))
        {
            var entryBytes = checked(entry.Key.Length + (entry.Value?.Length ?? 0) + EntryOverheadBytes);
            if (currentEntries.Count > 0 &&
                currentBytes + entryBytes > targetSizeBytes &&
                !ByteArrayComparer.Instance.Equals(currentEntries[^1].Key, entry.Key))
            {
                partitions.Add(CreatePartition(currentEntries, retainedRanges, regionStart, entry.Key));
                regionStart = entry.Key;
                currentEntries = [];
                currentBytes = 0;
            }

            currentEntries.Add(entry);
            currentBytes = checked(currentBytes + entryBytes);
        }

        if (currentEntries.Count > 0)
        {
            partitions.Add(CreatePartition(currentEntries, retainedRanges, regionStart, null));
        }
        else if (partitions.Count == 0 && retainedRanges.Length > 0)
        {
            return CompactionOutputPartitioner.Partition(
                new CompactionMergeResult([], retainedRanges),
                targetSizeBytes);
        }

        return partitions;
    }

    /// <summary>
    /// K-way merges every reader's sorted entry stream, grouping same-key entries across inputs
    /// (never within one input — an SST has no duplicate keys) and applying the same
    /// range-tombstone-covers / version-retention rules <c>CompactionMerger</c> applies, in the
    /// same order (ascending key, descending sequence within a key).
    /// </summary>
    static IEnumerable<SstEntry> MergeEntries(
        IReadOnlyList<SstReader> readers,
        ulong? horizon,
        bool pointTombstoneGcEligible,
        IReadOnlyList<RangeTombstone> droppedRanges)
    {
        var iterators = readers
            .Select(static reader => SstBlockIterator.Create(reader, PantsScanDirection.Forward))
            .ToArray();
        try
        {
            var heads = new SstEntry?[iterators.Length];
            for (var i = 0; i < iterators.Length; i++)
            {
                heads[i] = iterators[i].MoveNext() ? iterators[i].Current : null;
            }

            while (true)
            {
                byte[]? minKey = null;
                for (var i = 0; i < heads.Length; i++)
                {
                    var key = heads[i]?.Key;
                    if (key is not null &&
                        (minKey is null || ByteArrayComparer.Instance.Compare(key, minKey) < 0))
                    {
                        minKey = key;
                    }
                }

                if (minKey is null)
                {
                    yield break;
                }

                var group = new List<SstEntry>();
                for (var i = 0; i < heads.Length; i++)
                {
                    if (heads[i] is { } head && ByteArrayComparer.Instance.Equals(head.Key, minKey))
                    {
                        group.Add(head);
                        heads[i] = iterators[i].MoveNext() ? iterators[i].Current : null;
                    }
                }

                var survivingGroup = group.Where(entry => !droppedRanges.Any(range => Covers(range, entry)));
                foreach (var entry in RetainVersions(
                    survivingGroup.OrderByDescending(static entry => entry.Sequence),
                    horizon,
                    pointTombstoneGcEligible))
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            foreach (var iterator in iterators)
            {
                iterator.Dispose();
            }
        }
    }

    // Copied verbatim from CompactionMerger — see the type-level doc comment.
    static List<SstEntry> RetainVersions(
        IEnumerable<SstEntry> orderedEntries,
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

    static bool Covers(RangeTombstone range, SstEntry entry) =>
        entry.Sequence < range.Sequence &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.Start) >= 0 &&
        ByteArrayComparer.Instance.Compare(entry.Key, range.End) < 0;

    // Copied verbatim from CompactionOutputPartitioner.CreatePartition.
    static CompactionMergeResult CreatePartition(
        List<SstEntry> entries,
        IReadOnlyList<RangeTombstone> ranges,
        byte[]? regionStart,
        byte[]? regionEnd)
    {
        var overlappingRanges = ranges
            .Where(range =>
                (regionEnd is null || ByteArrayComparer.Instance.Compare(range.Start, regionEnd) < 0) &&
                (regionStart is null || ByteArrayComparer.Instance.Compare(range.End, regionStart) > 0))
            .Select(range => new RangeTombstone(
                regionStart is null || ByteArrayComparer.Instance.Compare(range.Start, regionStart) >= 0
                    ? range.Start.ToArray()
                    : regionStart.ToArray(),
                regionEnd is null || ByteArrayComparer.Instance.Compare(range.End, regionEnd) <= 0
                    ? range.End.ToArray()
                    : regionEnd.ToArray(),
                range.Sequence))
            .ToArray();
        return new CompactionMergeResult(entries.ToArray(), overlappingRanges);
    }
}
