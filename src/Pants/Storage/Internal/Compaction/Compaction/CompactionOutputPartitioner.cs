namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionOutputPartitioner
{
    const int EntryOverheadBytes = 32;
    const int RangeTombstoneOverheadBytes = 16;

    public static IReadOnlyList<CompactionMergeResult> Partition(
        CompactionMergeResult merged,
        long targetSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSizeBytes);
        if (merged.Entries.Count == 0)
        {
            return PartitionRangeTombstones(merged.RangeTombstones, targetSizeBytes);
        }

        var entryPartitions = new List<List<SstEntry>>();
        var entries = new List<SstEntry>();
        long estimatedBytes = 0;
        foreach (var entry in merged.Entries)
        {
            long entryBytes = checked(entry.Key.Length + (entry.Value?.Length ?? 0) + EntryOverheadBytes);
            if (entries.Count > 0 &&
                estimatedBytes + entryBytes > targetSizeBytes &&
                ByteArrayComparer.Instance.Compare(entries[^1].Key, entry.Key) != 0)
            {
                entryPartitions.Add(entries);
                entries = [];
                estimatedBytes = 0;
            }

            entries.Add(entry);
            estimatedBytes = checked(estimatedBytes + entryBytes);
        }

        entryPartitions.Add(entries);
        return entryPartitions.Select((partition, index) => CreatePartition(
            partition,
            merged.RangeTombstones,
            index == 0 ? null : partition[0].Key,
            index + 1 == entryPartitions.Count ? null : entryPartitions[index + 1][0].Key)).ToArray();
    }

    static List<CompactionMergeResult> PartitionRangeTombstones(
        IReadOnlyList<RangeTombstone> ranges,
        long targetSizeBytes)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        var partitions = new List<CompactionMergeResult>();
        var partition = new List<RangeTombstone>();
        long estimatedBytes = 0;
        foreach (var range in ranges)
        {
            long rangeBytes = checked(
                range.Start.Length + range.End.Length + RangeTombstoneOverheadBytes);
            if (partition.Count > 0 && estimatedBytes + rangeBytes > targetSizeBytes)
            {
                partitions.Add(new CompactionMergeResult([], partition.ToArray()));
                partition = [];
                estimatedBytes = 0;
            }

            partition.Add(range);
            estimatedBytes = checked(estimatedBytes + rangeBytes);
        }

        partitions.Add(new CompactionMergeResult([], partition.ToArray()));
        return partitions;
    }

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
