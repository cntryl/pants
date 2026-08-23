namespace Cntryl.Pants;

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

        var entryPartitions = new List<List<MidgeSstEntry>>();
        var entries = new List<MidgeSstEntry>();
        long estimatedBytes = 0;
        foreach (MidgeSstEntry entry in merged.Entries)
        {
            long entryBytes = checked(entry.Key.Length + (entry.Value?.Length ?? 0) + EntryOverheadBytes);
            if (entries.Count > 0 && estimatedBytes + entryBytes > targetSizeBytes)
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

    private static CompactionMergeResult CreatePartition(
        List<MidgeSstEntry> entries,
        IReadOnlyList<MidgeRangeTombstone> ranges,
        byte[]? regionStart,
        byte[]? regionEnd)
    {
        MidgeRangeTombstone[] overlappingRanges = ranges
            .Where(range =>
                (regionEnd is null || ByteArrayComparer.Instance.Compare(range.Start, regionEnd) < 0) &&
                (regionStart is null || ByteArrayComparer.Instance.Compare(range.End, regionStart) > 0))
            .Select(range => new MidgeRangeTombstone(
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
