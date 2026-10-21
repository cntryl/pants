namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class LeveledCompactionPlanner
{
    /// <summary>
    ///     Picks the next compaction. A forced pick also rewrites L0 once it holds more than one file
    ///     and any inner level holding more than one file, even below its size target.
    /// </summary>
    public static CompactionPlan? Pick(
        IReadOnlyList<FileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration,
        long? snapshotHorizon,
        bool force,
        long? preferredWorkBytes = null) =>
        PickCore(
            files,
            columnFamilyId,
            configuration,
            snapshotHorizon,
            l0ForceFloor: force ? 2 : int.MaxValue,
            innerForceFloor: force ? 2 : int.MaxValue,
            preferredWorkBytes);

    /// <summary>
    ///     Picks the next step of a manual debt drain. Any L0 file is drained, and inner levels are
    ///     compacted only while overfull, so under-target levels are never rewritten.
    /// </summary>
    public static CompactionPlan? PickDebtDrain(
        IReadOnlyList<FileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration,
        long? snapshotHorizon,
        long? preferredWorkBytes = null) =>
        PickCore(
            files,
            columnFamilyId,
            configuration,
            snapshotHorizon,
            l0ForceFloor: 1,
            innerForceFloor: int.MaxValue,
            preferredWorkBytes);

    /// <summary>
    ///     Shared selection. A level is forced once it holds at least its floor in files; a floor of
    ///     <see cref="int.MaxValue" /> disables forcing for that level.
    /// </summary>
    static CompactionPlan? PickCore(
        IReadOnlyList<FileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration,
        long? snapshotHorizon,
        int l0ForceFloor,
        int innerForceFloor,
        long? preferredWorkBytes)
    {
        ValidateConfiguration(configuration);
        if (preferredWorkBytes is <= 0)
        {
            throw PantsException.InvalidArgument("The preferred compaction work target must be positive.");
        }

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
            l0Files.Length >= l0ForceFloor)
        {
            var source = l0Files
                .OrderBy(static file => file.SstSequence)
                .Take(Math.Min(l0Files.Length, configuration.L0FileCountTrigger))
                .ToArray();
            return CreatePlan(familyFiles, source, FilesAtLevel(familyFiles, 1), 0, 1,
                columnFamilyId, configuration.MaximumInputFiles, snapshotHorizon, null);
        }

        for (uint level = 1; level < configuration.MaximumLevels - 1; level++)
        {
            var sourceLevelFiles = FilesAtLevel(familyFiles, level);
            if (IsOverfull(sourceLevelFiles, configuration, level) ||
                sourceLevelFiles.Length >= innerForceFloor)
            {
                var source = sourceLevelFiles
                    .OrderBy(GetSmallestKey, ByteArrayComparer.Instance)
                    .ThenBy(static file => file.Name, StringComparer.Ordinal)
                    .Take(configuration.L0FileCountTrigger)
                    .ToArray();
                return CreatePlan(familyFiles, source, FilesAtLevel(familyFiles, level + 1), level,
                    level + 1, columnFamilyId, configuration.MaximumInputFiles, snapshotHorizon,
                    preferredWorkBytes);
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns whether an inner level's total size exceeds its target.
    /// </summary>
    static bool IsOverfull(
        IReadOnlyList<FileMeta> levelFiles,
        PantsCompactionConfiguration configuration,
        uint level)
    {
        var levelSize = levelFiles.Aggregate(0UL, static (total, file) =>
            SaturatingAdd(total, file.SizeBytes));
        return levelSize > LevelTargetBytes(configuration, level);
    }

    /// <summary>
    ///     Returns the size target for an inner level, which grows by the level multiplier per level.
    /// </summary>
    public static ulong LevelTargetBytes(PantsCompactionConfiguration configuration, uint level)
    {
        var targetSize = checked((ulong)configuration.L1TargetSizeBytes);
        for (uint current = 1; current < level; current++)
        {
            targetSize = SaturatingMultiply(targetSize, checked((ulong)configuration.LevelMultiplier));
        }

        return targetSize;
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
    ///     Checks that no target-level file was added to or removed from the span captured by a
    ///     compaction plan while its worker was merging.
    /// </summary>
    public static bool HasSameTargetSpan(
        CompactionPlan plan,
        IReadOnlyList<FileMeta> currentFiles)
    {
        if (plan.Purpose == CompactionPurpose.Fragment)
        {
            return HasSameFragmentBoundary(plan, currentFiles);
        }

        var sources = plan.Inputs
            .Where(file => file.Level == plan.SourceLevel)
            .ToArray();
        if (sources.Length == 0)
        {
            return false;
        }

        var familyFiles = currentFiles
            .Where(file => file.ColumnFamilyId == plan.ColumnFamilyId)
            .ToArray();
        var expectedTargetNames = plan.Inputs
            .Where(file => file.Level == plan.TargetLevel)
            .Select(static file => file.Name)
            .ToHashSet(StringComparer.Ordinal);
        var currentTargetNames = ExpandToCompleteTargetSpan(
                sources,
                FilesAtLevel(familyFiles, plan.TargetLevel))
            .Where(file => file.Level == plan.TargetLevel)
            .Select(static file => file.Name)
            .ToHashSet(StringComparer.Ordinal);
        return expectedTargetNames.SetEquals(currentTargetNames);
    }

    static bool HasSameFragmentBoundary(CompactionPlan plan, IReadOnlyList<FileMeta> currentFiles)
    {
        if (plan.SourceLevel == 0 || plan.TargetLevel != plan.SourceLevel ||
            plan.Inputs.Count != 1 || plan.Inputs[0].Level != plan.SourceLevel ||
            plan.FragmentSplitKey is not { Length: > 0 } splitKey)
        {
            return false;
        }

        var familyFiles = currentFiles
            .Where(file => file.ColumnFamilyId == plan.ColumnFamilyId)
            .ToArray();
        var source = familyFiles.SingleOrDefault(file => file.Name == plan.Inputs[0].Name);
        if (source is null || !source.HasTrustedKeyBounds() ||
            ByteArrayComparer.Instance.Compare(source.SmallestKey, splitKey) >= 0 ||
            ByteArrayComparer.Instance.Compare(splitKey, source.LargestKey) >= 0)
        {
            return false;
        }

        return TryGetFragmentationSplitKey(
                   source,
                   FilesAtLevel(familyFiles, plan.SourceLevel),
                   FilesAtLevel(familyFiles, plan.SourceLevel + 1),
                   out var currentSplitKey) &&
               ByteArrayComparer.Instance.Equals(splitKey, currentSplitKey);
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
        long? snapshotHorizon,
        long? preferredWorkBytes)
    {
        var sources = sourceFiles.ToList();
        List<FileMeta> selected;
        while (true)
        {
            selected = ExpandToCompleteTargetSpan(sources, targetFiles);
            var overFileLimit = selected.Count > maximumInputs;
            if (!overFileLimit || sources.Count == 1)
            {
                break;
            }

            sources.RemoveAt(sources.Count - 1);
        }

        if (sources.Count > 1 && preferredWorkBytes is { } workTarget &&
            ExceedsWorkTarget(selected, workTarget))
        {
            sources.RemoveRange(1, sources.Count - 1);
            selected = ExpandToCompleteTargetSpan(sources, targetFiles);
        }

        if (sources.Count == 1 && preferredWorkBytes is { } targetBytes &&
            ExceedsWorkTarget(selected, targetBytes) &&
            TryGetFragmentationSplitKey(
                sources[0],
                FilesAtLevel(familyFiles, sourceLevel),
                FilesAtLevel(familyFiles, targetLevel),
                out var splitKey))
        {
            return new CompactionPlan(sourceLevel, sourceLevel, columnFamilyId, null,
                false, false, [sources[0]], CompactionPurpose.Fragment, splitKey);
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

    static bool ExceedsWorkTarget(IReadOnlyList<FileMeta> files, long workTargetBytes)
    {
        ulong total = 0;
        foreach (var file in files)
        {
            if (ulong.MaxValue - total < file.SizeBytes)
            {
                return true;
            }

            total += file.SizeBytes;
        }

        return total > checked((ulong)workTargetBytes);
    }

    static bool TryGetFragmentationSplitKey(
        FileMeta source,
        IReadOnlyList<FileMeta> sourceLevelFiles,
        IReadOnlyList<FileMeta> targetLevelFiles,
        out byte[] splitKey)
    {
        splitKey = [];
        if (!sourceLevelFiles.All(static file => file.HasTrustedKeyBounds()) ||
            !targetLevelFiles.All(static file => file.HasTrustedKeyBounds()) ||
            HasOverlappingFiles(sourceLevelFiles) || HasOverlappingFiles(targetLevelFiles))
        {
            return false;
        }

        var smallest = GetSmallestKey(source);
        var largest = GetLargestKey(source);
        var siblingBoundaries = sourceLevelFiles
            .Where(file => !string.Equals(file.Name, source.Name, StringComparison.Ordinal))
            .SelectMany(static file => new[] { GetSmallestKey(file), GetLargestKey(file) })
            .ToHashSet(ByteArrayComparer.Instance);
        var cuts = targetLevelFiles
            .Select(GetSmallestKey)
            .Where(key => ByteArrayComparer.Instance.Compare(smallest, key) < 0 &&
                          ByteArrayComparer.Instance.Compare(key, largest) < 0 &&
                          !siblingBoundaries.Contains(key))
            .Distinct(ByteArrayComparer.Instance)
            .Order(ByteArrayComparer.Instance)
            .ToArray();
        if (cuts.Length == 0)
        {
            return false;
        }

        splitKey = cuts[cuts.Length / 2].ToArray();
        return true;
    }

    static bool HasOverlappingFiles(IReadOnlyList<FileMeta> files)
    {
        var ordered = files.OrderBy(GetSmallestKey, ByteArrayComparer.Instance).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ByteArrayComparer.Instance.Compare(
                    GetSmallestKey(ordered[index]), GetLargestKey(ordered[index - 1])) < 0)
            {
                return true;
            }
        }

        return false;
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

    static ulong SaturatingAdd(ulong value, ulong addend) =>
        ulong.MaxValue - value < addend ? ulong.MaxValue : value + addend;

    static byte[] GetSmallestKey(FileMeta file) =>
        file.SmallestKey!;

    static byte[] GetLargestKey(FileMeta file) =>
        file.LargestKey!;

    static bool Overlaps(byte[] leftSmallest, byte[] leftLargest, byte[] rightSmallest,
        byte[] rightLargest) =>
        ByteArrayComparer.Instance.Compare(leftSmallest, rightLargest) <= 0 &&
        ByteArrayComparer.Instance.Compare(rightSmallest, leftLargest) <= 0;
}
