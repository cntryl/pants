namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
/// Incremental counterpart to <c>CompactionMerger.Merge</c> + <c>CompactionOutputPartitioner.Partition</c>:
/// does the same k-way merge across inputs, the same version-retention/tombstone-covers-entry/
/// GC-eligibility filtering, and the same size-based output partitioning with per-partition
/// range-tombstone clamping — but drives it entry-by-entry over one <see cref="SstBlockIterator"/>
/// per input and yields each completed partition before building the next instead of
/// materializing every input, merged result, and output partition concurrently. Range
/// tombstones remain a small, resident-per-file list (loaded eagerly by
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
    const int RangeTombstoneOverheadBytes = 24;
    const long ReservationGranularityBytes = 64 * 1024;

    public static IEnumerable<CompactionMergeResult> MergeAndPartition(
        IReadOnlyList<SstReader> readers,
        CompactionPlan plan,
        long targetSizeBytes,
        ResourceBudget? resourceBudget = null)
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
            if (retainedRanges.Length > 0)
            {
                foreach (var partition in CompactionOutputPartitioner.Partition(
                             new CompactionMergeResult([], retainedRanges),
                             targetSizeBytes))
                {
                    using var reservation = resourceBudget?.Reserve(
                        EstimatePartitionBytes(partition));
                    yield return partition;
                }
            }

            yield break;
        }

        var effectiveTargetSizeBytes = resourceBudget is null
            ? targetSizeBytes
            : Math.Min(
                targetSizeBytes,
                Math.Max(1, resourceBudget.Limit / 2));
        var currentEntries = new List<SstEntry>();
        var reservations = new List<IDisposable>();
        long reservedBytes = 0;
        long currentBytes = 0;
        byte[]? regionStart = null;
        try
        {
            foreach (var entry in MergeEntries(
                         readers,
                         horizon,
                         plan.PointTombstoneGcEligible,
                         droppedRanges,
                         resourceBudget))
            {
                var entryBytes = checked(
                    entry.Key.Length +
                    (entry.Value?.Length ?? 0) +
                    EntryOverheadBytes);
                if (entryBytes > effectiveTargetSizeBytes)
                {
                    throw PantsException.ResourceLimit(
                        $"A {entryBytes}-byte compaction entry exceeds the " +
                        $"{effectiveTargetSizeBytes}-byte compaction buffer budget.");
                }

                if (currentEntries.Count > 0 &&
                    currentBytes + entryBytes > effectiveTargetSizeBytes &&
                    !ByteArrayComparer.Instance.Equals(currentEntries[^1].Key, entry.Key))
                {
                    var partition = CreatePartition(
                        currentEntries,
                        retainedRanges,
                        regionStart,
                        entry.Key);
                    ReserveThrough(
                        resourceBudget,
                        reservations,
                        ref reservedBytes,
                        EstimatePartitionBytes(partition));
                    yield return partition;
                    DisposeReservations(reservations);
                    reservedBytes = 0;
                    regionStart = entry.Key;
                    currentEntries = [];
                    currentBytes = 0;
                }

                ReserveThrough(
                    resourceBudget,
                    reservations,
                    ref reservedBytes,
                    checked(currentBytes + entryBytes));
                currentEntries.Add(entry);
                currentBytes = checked(currentBytes + entryBytes);
            }

            if (currentEntries.Count > 0)
            {
                var partition = CreatePartition(currentEntries, retainedRanges, regionStart, null);
                ReserveThrough(
                    resourceBudget,
                    reservations,
                    ref reservedBytes,
                    EstimatePartitionBytes(partition));
                yield return partition;
            }
            else if (retainedRanges.Length > 0)
            {
                foreach (var partition in CompactionOutputPartitioner.Partition(
                             new CompactionMergeResult([], retainedRanges),
                             effectiveTargetSizeBytes))
                {
                    ReserveThrough(
                        resourceBudget,
                        reservations,
                        ref reservedBytes,
                        EstimatePartitionBytes(partition));
                    yield return partition;
                    DisposeReservations(reservations);
                    reservedBytes = 0;
                }
            }
        }
        finally
        {
            DisposeReservations(reservations);
        }
    }

    static long EstimatePartitionBytes(CompactionMergeResult partition) => checked(
        partition.Entries.Sum(static entry =>
            (long)entry.Key.Length + (entry.Value?.Length ?? 0) + EntryOverheadBytes) +
        partition.RangeTombstones.Sum(static range =>
            (long)range.Start.Length + range.End.Length + RangeTombstoneOverheadBytes));

    static void ReserveThrough(
        ResourceBudget? budget,
        List<IDisposable> reservations,
        ref long reservedBytes,
        long requiredBytes)
    {
        if (budget is null)
        {
            return;
        }

        if (requiredBytes > budget.Limit)
        {
            throw PantsException.ResourceLimit(
                $"A {requiredBytes}-byte compaction partition exceeds the " +
                $"{budget.Limit}-byte compaction buffer budget.");
        }

        while (reservedBytes < requiredBytes)
        {
            var bytes = Math.Min(ReservationGranularityBytes, requiredBytes - reservedBytes);
            reservations.Add(budget.Reserve(bytes));
            reservedBytes = checked(reservedBytes + bytes);
        }
    }

    static void DisposeReservations(List<IDisposable> reservations)
    {
        foreach (var reservation in reservations)
        {
            reservation.Dispose();
        }

        reservations.Clear();
    }

    /// <summary>
    /// K-way merges every reader's sorted entry stream, grouping same-key entries both across
    /// inputs and within one input. Flush SSTs may contain multiple versions of a key, so every
    /// matching head must be drained into one retention group before applying the same
    /// range-tombstone-covers / version-retention rules <c>CompactionMerger</c> applies, in the
    /// same order (ascending key, descending sequence within a key).
    /// </summary>
    static IEnumerable<SstEntry> MergeEntries(
        IReadOnlyList<SstReader> readers,
        ulong? horizon,
        bool pointTombstoneGcEligible,
        IReadOnlyList<RangeTombstone> droppedRanges,
        ResourceBudget? resourceBudget)
    {
        var iterators = readers
            .Select(reader => SstBlockIterator.Create(
                reader,
                PantsScanDirection.Forward,
                resourceBudget: resourceBudget))
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
                    while (heads[i] is { } head &&
                           ByteArrayComparer.Instance.Equals(head.Key, minKey))
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
