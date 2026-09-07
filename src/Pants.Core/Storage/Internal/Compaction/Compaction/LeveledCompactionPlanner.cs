namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class LeveledCompactionPlanner
{
    public static CompactionPlan? Pick(
        IReadOnlyList<FileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration,
        long? snapshotHorizon,
        bool force)
    {
        ValidateConfiguration(configuration);
        var familyFiles = files.Where(file => file.ColumnFamilyId == columnFamilyId).ToArray();
        ValidateMetadata(familyFiles, columnFamilyId, configuration.MaximumLevels);
        if (familyFiles.Length == 0)
        {
            return null;
        }

        var l0Files = FilesAtLevel(familyFiles, 0);
        var l0Size = l0Files.Aggregate(0UL, static (total, file) => checked(total + file.SizeBytes));
        if (l0Size > checked((ulong)configuration.L0SizeTriggerBytes) ||
            l0Files.Length >= configuration.L0FileCountTrigger ||
            (force && l0Files.Length > 1))
        {
            var source = l0Files
                .OrderBy(static file => file.SstSequence)
                .Take(Math.Min(l0Files.Length, configuration.L0FileCountTrigger))
                .ToArray();
            return CreatePlan(familyFiles, source, FilesAtLevel(familyFiles, 1), 0, 1,
                columnFamilyId, configuration.MaximumInputFiles, snapshotHorizon);
        }

        var targetSize = checked((ulong)configuration.L1TargetSizeBytes);
        for (uint level = 1; level < configuration.MaximumLevels - 1; level++)
        {
            var sourceLevelFiles = FilesAtLevel(familyFiles, level);
            var levelSize = sourceLevelFiles.Aggregate(0UL, static (total, file) =>
                checked(total + file.SizeBytes));
            if (levelSize > targetSize || (force && sourceLevelFiles.Length > 1))
            {
                var source = sourceLevelFiles
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

    /// <summary>
    ///     Adds every target-level file overlapping the source key range, to a fixed point, since
    ///     each addition can widen the range and pull in further files.
    /// </summary>
    static List<FileMeta> ExpandToCompleteTargetSpan(
        IReadOnlyList<FileMeta> sources,
        IReadOnlyList<FileMeta> targetFiles)
    {
        var selected = sources.ToList();
        var selectedNames = sources
            .Select(static file => file.Name)
            .ToHashSet(StringComparer.Ordinal);
        var smallest = sources.Select(GetSmallestKey).Min(ByteArrayComparer.Instance)!;
        var largest = sources.Select(GetLargestKey).Max(ByteArrayComparer.Instance)!;

        bool changed;
        do
        {
            changed = false;
            foreach (var target in targetFiles)
            {
                if (selectedNames.Contains(target.Name))
                {
                    continue;
                }

                var targetSmallest = GetSmallestKey(target);
                var targetLargest = GetLargestKey(target);
                if (!Overlaps(smallest, largest, targetSmallest, targetLargest))
                {
                    continue;
                }

                selected.Add(target);
                selectedNames.Add(target.Name);
                if (ByteArrayComparer.Instance.Compare(targetSmallest, smallest) < 0)
                {
                    smallest = targetSmallest;
                }

                if (ByteArrayComparer.Instance.Compare(targetLargest, largest) > 0)
                {
                    largest = targetLargest;
                }

                changed = true;
            }
        } while (changed);

        return selected;
    }

    /// <summary>
    ///     Builds a plan whose source set is bounded by <paramref name="maximumInputs" /> but whose
    ///     target span is always complete.
    /// </summary>
    /// <remarks>
    ///     The target span cannot be truncated: dropping an overlapping target file would publish an
    ///     output that overlaps a file left behind at the same level. Only the source set can shrink,
    ///     so a wide overlap closure narrows the sources instead of abandoning the plan. Abandoning
    ///     is not an option — write admission stalls on L0 debt that only compaction can drain, so a
    ///     planner that declines to plan turns backpressure into a deadlock. A single source file
    ///     plus its complete target span is always legal and always makes progress, so that is the
    ///     floor; at that point <paramref name="maximumInputs" /> is advisory rather than a cap.
    ///     Sources arrive in priority order — oldest first at L0, key order below it — so shrinking
    ///     drops from the end and never violates recency.
    /// </remarks>
    static CompactionPlan? CreatePlan(
        FileMeta[] familyFiles,
        IReadOnlyList<FileMeta> sourceFiles,
        IReadOnlyList<FileMeta> targetFiles,
        uint sourceLevel,
        uint targetLevel,
        uint columnFamilyId,
        int maximumInputs,
        long? snapshotHorizon)
    {
        var sources = sourceFiles.ToList();
        List<FileMeta> selected;
        while (true)
        {
            selected = ExpandToCompleteTargetSpan(sources, targetFiles);
            if (selected.Count <= maximumInputs || sources.Count == 1)
            {
                break;
            }

            sources.RemoveAt(sources.Count - 1);
        }

        var selectedSmallest = selected.Select(GetSmallestKey).Min(ByteArrayComparer.Instance)!;
        var selectedLargest = selected.Select(GetLargestKey).Max(ByteArrayComparer.Instance)!;
        var selectedNames = selected.Select(static file => file.Name).ToHashSet(StringComparer.Ordinal);
        var pointEligible = !familyFiles.Any(file =>
            !selectedNames.Contains(file.Name) &&
            Overlaps(selectedSmallest, selectedLargest, GetSmallestKey(file), GetLargestKey(file)));
        var rangeEligible = pointEligible && selected.Count == familyFiles.Length;

        return new CompactionPlan(sourceLevel, targetLevel, columnFamilyId, snapshotHorizon,
            pointEligible, rangeEligible, selected
                .OrderBy(static file => file.Level)
                .ThenBy(static file => file.SstSequence)
                .ThenBy(static file => file.Name, StringComparer.Ordinal)
                .ToArray());
    }

    static FileMeta[] FilesAtLevel(IEnumerable<FileMeta> files, uint level) =>
        files.Where(file => file.Level == level).ToArray();

    static void ValidateConfiguration(PantsCompactionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.L0SizeTriggerBytes <= 0 || configuration.L0FileCountTrigger <= 0 ||
            configuration.MaximumInputFiles <= 0 || configuration.LevelMultiplier <= 1 ||
            configuration.L1TargetSizeBytes <= 0 || configuration.MaximumLevels < 2)
        {
            throw PantsException.InvalidArgument("Compaction limits are invalid.");
        }
    }

    static void ValidateMetadata(IEnumerable<FileMeta> files, uint columnFamilyId,
        int maximumLevels)
    {
        foreach (var file in files)
        {
            if (file.Level >= maximumLevels || file.SmallestKey is null || file.LargestKey is null ||
                ByteArrayComparer.Instance.Compare(GetSmallestKey(file), GetLargestKey(file)) > 0)
            {
                throw PantsException.Create(PantsErrorCode.Corruption,
                    $"SST '{file.Name}' for column family {columnFamilyId} has invalid compaction metadata.");
            }
        }
    }

    static ulong SaturatingMultiply(ulong value, ulong multiplier) =>
        value > ulong.MaxValue / multiplier ? ulong.MaxValue : value * multiplier;

    static byte[] GetSmallestKey(FileMeta file) =>
        file.SmallestKey!.Select(static value => checked((byte)value)).ToArray();

    static byte[] GetLargestKey(FileMeta file) =>
        file.LargestKey!.Select(static value => checked((byte)value)).ToArray();

    static bool Overlaps(byte[] leftSmallest, byte[] leftLargest, byte[] rightSmallest,
        byte[] rightLargest) =>
        ByteArrayComparer.Instance.Compare(leftSmallest, rightLargest) <= 0 &&
        ByteArrayComparer.Instance.Compare(rightSmallest, leftLargest) <= 0;
}
