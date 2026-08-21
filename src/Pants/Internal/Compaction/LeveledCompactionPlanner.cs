namespace Pants;

internal static class LeveledCompactionPlanner
{
    private const int LevelMultiplier = 10;
    private const uint MaximumLevel = 6;

    public static CompactionPlan? Pick(
        IReadOnlyList<MidgeFileMeta> files,
        uint columnFamilyId,
        int l0FileTrigger,
        long l1TargetBytes,
        int maximumInputs,
        bool force)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(l0FileTrigger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(l1TargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInputs);

        MidgeFileMeta[] familyFiles = files
            .Where(file => file.ColumnFamilyId == columnFamilyId)
            .ToArray();
        MidgeFileMeta[] l0Files = familyFiles
            .Where(static file => file.Level == 0)
            .OrderBy(static file => file.SstSequence)
            .ToArray();
        if (l0Files.Length >= l0FileTrigger || force && l0Files.Length > 1)
        {
            if (l0Files.Length > maximumInputs)
            {
                l0Files = l0Files[..maximumInputs];
            }

            return CloseOverlaps(
                l0Files,
                familyFiles.Where(static file => file.Level == 1),
                sourceLevel: 0,
                targetLevel: 1,
                maximumInputs);
        }

        long levelTarget = l1TargetBytes;
        for (uint level = 1; level < MaximumLevel; level++)
        {
            MidgeFileMeta[] source = familyFiles
                .Where(file => file.Level == level)
                .OrderBy(static file => file.SstSequence)
                .ToArray();
            if (source.Aggregate(0UL, static (total, file) => total + file.SizeBytes) >
                checked((ulong)levelTarget))
            {
                return CloseOverlaps(
                    source.Take(1),
                    familyFiles.Where(file => file.Level == level + 1),
                    level,
                    level + 1,
                    maximumInputs);
            }

            levelTarget = checked(levelTarget * LevelMultiplier);
        }

        return null;
    }

    private static CompactionPlan? CloseOverlaps(
        IEnumerable<MidgeFileMeta> sourceFiles,
        IEnumerable<MidgeFileMeta> targetFiles,
        uint sourceLevel,
        uint targetLevel,
        int maximumInputs)
    {
        var selected = sourceFiles.ToList();
        if (selected.Count == 0 || selected.Any(static file => !HasBounds(file)))
        {
            return null;
        }

        MidgeFileMeta[] targets = targetFiles
            .OrderBy(static file => file.SstSequence)
            .ToArray();
        bool changed;
        do
        {
            changed = false;
            byte[] smallest = selected
                .Select(GetSmallestKey)
                .Min(ByteArrayComparer.Instance)!;
            byte[] largest = selected
                .Select(GetLargestKey)
                .Max(ByteArrayComparer.Instance)!;
            foreach (MidgeFileMeta target in targets)
            {
                if (selected.Contains(target) || !HasBounds(target) ||
                    !Overlaps(smallest, largest, GetSmallestKey(target), GetLargestKey(target)))
                {
                    continue;
                }

                selected.Add(target);
                if (selected.Count > maximumInputs)
                {
                    return null;
                }

                changed = true;
            }
        }
        while (changed);

        return new CompactionPlan(
            sourceLevel,
            targetLevel,
            selected
                .OrderBy(static file => file.Level)
                .ThenBy(static file => file.SstSequence)
                .ToArray());
    }

    private static bool HasBounds(MidgeFileMeta file) =>
        file.SmallestKey is not null && file.LargestKey is not null;

    private static byte[] GetSmallestKey(MidgeFileMeta file) =>
        file.SmallestKey!.Select(static value => checked((byte)value)).ToArray();

    private static byte[] GetLargestKey(MidgeFileMeta file) =>
        file.LargestKey!.Select(static value => checked((byte)value)).ToArray();

    private static bool Overlaps(
        byte[] leftSmallest,
        byte[] leftLargest,
        byte[] rightSmallest,
        byte[] rightLargest) =>
        ByteArrayComparer.Instance.Compare(leftSmallest, rightLargest) <= 0 &&
        ByteArrayComparer.Instance.Compare(rightSmallest, leftLargest) <= 0;
}
