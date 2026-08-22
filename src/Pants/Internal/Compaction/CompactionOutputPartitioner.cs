namespace Pants;

internal static class CompactionOutputPartitioner
{
    private const int EntryOverheadBytes = 32;

    public static IReadOnlyList<CompactionMergeResult> Partition(
        CompactionMergeResult merged,
        long targetSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSizeBytes);
        if (merged.Entries.Count == 0)
        {
            return merged.RangeTombstones.Count == 0 ? [] : [merged];
        }

        var partitions = new List<CompactionMergeResult>();
        var entries = new List<MidgeSstEntry>();
        long estimatedBytes = 0;
        foreach (MidgeSstEntry entry in merged.Entries)
        {
            long entryBytes = checked(entry.Key.Length + (entry.Value?.Length ?? 0) + EntryOverheadBytes);
            if (entries.Count > 0 && estimatedBytes + entryBytes > targetSizeBytes)
            {
                partitions.Add(CreatePartition(entries, merged.RangeTombstones));
                entries = [];
                estimatedBytes = 0;
            }

            entries.Add(entry);
            estimatedBytes = checked(estimatedBytes + entryBytes);
        }

        partitions.Add(CreatePartition(entries, merged.RangeTombstones));
        return partitions;
    }

    private static CompactionMergeResult CreatePartition(
        List<MidgeSstEntry> entries,
        IReadOnlyList<MidgeRangeTombstone> ranges)
    {
        byte[] smallest = entries[0].Key;
        byte[] largest = entries[^1].Key;
        MidgeRangeTombstone[] overlappingRanges = ranges
            .Where(range =>
                ByteArrayComparer.Instance.Compare(range.Start, largest) <= 0 &&
                ByteArrayComparer.Instance.Compare(range.End, smallest) > 0)
            .ToArray();
        return new CompactionMergeResult(entries.ToArray(), overlappingRanges);
    }
}
