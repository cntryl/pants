namespace Cntryl.Pants;

internal static class LeveledCompactionPlanner
{
    public static CompactionPlan? Pick(
        IReadOnlyList<MidgeFileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration,
        long? snapshotHorizon,
        bool force)
    {
        ValidateConfiguration(configuration);
        MidgeFileMeta[] familyFiles = files.Where(file => file.ColumnFamilyId == columnFamilyId).ToArray();
        ValidateMetadata(familyFiles, columnFamilyId, configuration.MaximumLevels);
        if (familyFiles.Length == 0)
        {
            return null;
        }

        MidgeFileMeta[] l0Files = FilesAtLevel(familyFiles, 0);
        ulong l0Size = l0Files.Aggregate(0UL, static (total, file) => checked(total + file.SizeBytes));
        if (l0Size > checked((ulong)configuration.L0SizeTriggerBytes) ||
            l0Files.Length >= configuration.L0FileCountTrigger ||
            force && l0Files.Length > 1)
        {
            MidgeFileMeta[] source = l0Files
                .OrderBy(static file => file.SstSequence)
                .Take(Math.Min(l0Files.Length, configuration.L0FileCountTrigger))
                .ToArray();
            return CreatePlan(familyFiles, source, FilesAtLevel(familyFiles, 1), 0, 1,
                columnFamilyId, configuration.MaximumInputFiles, snapshotHorizon);
        }

        ulong targetSize = checked((ulong)configuration.L1TargetSizeBytes);
        for (uint level = 1; level < configuration.MaximumLevels - 1; level++)
        {
            MidgeFileMeta[] sourceLevelFiles = FilesAtLevel(familyFiles, level);
            ulong levelSize = sourceLevelFiles.Aggregate(0UL, static (total, file) =>
                checked(total + file.SizeBytes));
            if (levelSize > targetSize || force && sourceLevelFiles.Length > 1)
            {
                MidgeFileMeta[] source = sourceLevelFiles
                    .OrderBy(GetSmallestKey, ByteArrayComparer.Instance)
                    .ThenBy(static file => file.Name, StringComparer.Ordinal)
                    .Take(configuration.L0FileCountTrigger)
                    .ToArray();
                return CreatePlan(familyFiles, source, FilesAtLevel(familyFiles, level + 1), level,
                    level + 1, columnFamilyId, configuration.MaximumInputFiles, snapshotHorizon);
            }

            targetSize = SaturatingMultiply(targetSize, checked((ulong)configuration.LevelMultiplier));
        }

        return null;
    }

    private static CompactionPlan CreatePlan(
        MidgeFileMeta[] familyFiles,
        IReadOnlyList<MidgeFileMeta> sourceFiles,
        IReadOnlyList<MidgeFileMeta> targetFiles,
        uint sourceLevel,
        uint targetLevel,
        uint columnFamilyId,
        int maximumInputs,
        long? snapshotHorizon)
    {
        var selected = sourceFiles.ToList();
        bool changed;
        do
        {
            changed = false;
            byte[] smallest = selected.Select(GetSmallestKey).Min(ByteArrayComparer.Instance)!;
            byte[] largest = selected.Select(GetLargestKey).Max(ByteArrayComparer.Instance)!;
            foreach (MidgeFileMeta target in targetFiles)
            {
                if (selected.Contains(target) ||
                    !Overlaps(smallest, largest, GetSmallestKey(target), GetLargestKey(target)))
                {
                    continue;
                }

                selected.Add(target);
                changed = true;
            }
        }
        while (changed);

        if (selected.Count > maximumInputs)
        {
            throw PantsException.ResourceLimit(
                $"Compaction L{sourceLevel}->L{targetLevel} for column family {columnFamilyId} " +
                $"requires {selected.Count} inputs, exceeding the configured limit {maximumInputs}.");
        }

        byte[] selectedSmallest = selected.Select(GetSmallestKey).Min(ByteArrayComparer.Instance)!;
        byte[] selectedLargest = selected.Select(GetLargestKey).Max(ByteArrayComparer.Instance)!;
        var selectedNames = selected.Select(static file => file.Name).ToHashSet(StringComparer.Ordinal);
        bool pointEligible = !familyFiles.Any(file =>
            !selectedNames.Contains(file.Name) &&
            Overlaps(selectedSmallest, selectedLargest, GetSmallestKey(file), GetLargestKey(file)));
        bool rangeEligible = pointEligible && selected.Count == familyFiles.Length;

        return new CompactionPlan(sourceLevel, targetLevel, columnFamilyId, snapshotHorizon,
            pointEligible, rangeEligible, selected
                .OrderBy(static file => file.Level)
                .ThenBy(static file => file.SstSequence)
                .ThenBy(static file => file.Name, StringComparer.Ordinal)
                .ToArray());
    }

    private static MidgeFileMeta[] FilesAtLevel(IEnumerable<MidgeFileMeta> files, uint level) =>
        files.Where(file => file.Level == level).ToArray();

    private static void ValidateConfiguration(PantsCompactionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.L0SizeTriggerBytes <= 0 || configuration.L0FileCountTrigger <= 0 ||
            configuration.MaximumInputFiles <= 0 || configuration.LevelMultiplier <= 1 ||
            configuration.L1TargetSizeBytes <= 0 || configuration.MaximumLevels < 2)
        {
            throw PantsException.InvalidArgument("Compaction limits are invalid.");
        }
    }

    private static void ValidateMetadata(IEnumerable<MidgeFileMeta> files, uint columnFamilyId,
        int maximumLevels)
    {
        foreach (MidgeFileMeta file in files)
        {
            if (file.Level >= maximumLevels || file.SmallestKey is null || file.LargestKey is null ||
                ByteArrayComparer.Instance.Compare(GetSmallestKey(file), GetLargestKey(file)) > 0)
            {
                throw PantsException.Create(PantsErrorCode.Corruption,
                    $"SST '{file.Name}' for column family {columnFamilyId} has invalid compaction metadata.");
            }
        }
    }

    private static ulong SaturatingMultiply(ulong value, ulong multiplier) =>
        value > ulong.MaxValue / multiplier ? ulong.MaxValue : value * multiplier;

    private static byte[] GetSmallestKey(MidgeFileMeta file) =>
        file.SmallestKey!.Select(static value => checked((byte)value)).ToArray();

    private static byte[] GetLargestKey(MidgeFileMeta file) =>
        file.LargestKey!.Select(static value => checked((byte)value)).ToArray();

    private static bool Overlaps(byte[] leftSmallest, byte[] leftLargest, byte[] rightSmallest,
        byte[] rightLargest) =>
        ByteArrayComparer.Instance.Compare(leftSmallest, rightLargest) <= 0 &&
        ByteArrayComparer.Instance.Compare(rightSmallest, leftLargest) <= 0;
}
