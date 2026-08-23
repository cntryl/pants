namespace Cntryl.Pants.Tests;

public sealed class LeveledCompactionPlannerTests
{
    [Fact]
    public void ShouldPickBoundedL0InputsAndEveryOverlappingL1File()
    {
        MidgeFileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-3.sst", 0, 3, "g", "i"),
            File("l1-1.sst", 1, 4, "b", "e"),
            File("l1-2.sst", 1, 5, "e", "h"),
            File("l1-unrelated.sst", 1, 6, "x", "z")
        ];

        CompactionPlan? plan = LeveledCompactionPlanner.Pick(
            files,
            columnFamilyId: 0,
            Configuration(l0FileCountTrigger: 3, maximumInputFiles: 5),
            snapshotHorizon: 42,
            force: false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.TargetLevel);
        Assert.Equal(42, plan.SnapshotHorizon);
        Assert.True(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
        Assert.Equal(
            ["l0-1.sst", "l0-2.sst", "l0-3.sst", "l1-1.sst", "l1-2.sst"],
            plan.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldFailClosedWhenOverlapClosureExceedsInputLimit()
    {
        MidgeFileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "f"),
            File("l1-2.sst", 1, 4, "g", "m"),
            File("l1-3.sst", 1, 5, "n", "z")
        ];

        PantsException exception = Assert.ThrowsAny<PantsException>(() =>
            LeveledCompactionPlanner.Pick(
                files,
                columnFamilyId: 0,
                Configuration(l0FileCountTrigger: 2, maximumInputFiles: 4),
                snapshotHorizon: null,
                force: false));

        Assert.Equal(PantsErrorCode.ResourceLimit, exception.Code);
    }

    [Fact]
    public void ShouldTriggerL0CompactionByBytesIndependentlyOfFileCount()
    {
        MidgeFileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z", sizeBytes: 1025)
        ];

        CompactionPlan? plan = LeveledCompactionPlanner.Pick(
            files,
            columnFamilyId: 0,
            Configuration(l0SizeTriggerBytes: 1024, l0FileCountTrigger: 10),
            snapshotHorizon: null,
            force: false);

        Assert.NotNull(plan);
    }

    [Fact]
    public void ShouldTriggerInnerLevelByConfiguredSizeRatio()
    {
        MidgeFileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "m", sizeBytes: 1025),
            File("l2-1.sst", 2, 2, "n", "z")
        ];

        CompactionPlan? plan = LeveledCompactionPlanner.Pick(
            files,
            columnFamilyId: 0,
            Configuration(l1TargetSizeBytes: 1024, levelMultiplier: 3),
            snapshotHorizon: null,
            force: false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
        Assert.Equal(2u, plan.TargetLevel);
    }

    [Fact]
    public void ShouldMakePointAndRangeTombstoneEligibilityDistinct()
    {
        MidgeFileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-unselected.sst", 0, 2, "x", "z")
        ];

        CompactionPlan? plan = LeveledCompactionPlanner.Pick(
            files,
            columnFamilyId: 0,
            Configuration(l0FileCountTrigger: 1, maximumInputFiles: 1),
            snapshotHorizon: 7,
            force: false);

        Assert.NotNull(plan);
        Assert.True(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
    }

    [Fact]
    public void ShouldRejectInvalidFileMetadataAsCorruption()
    {
        MidgeFileMeta invalid = File("invalid.sst", 7, 1, "z", "a");

        PantsException exception = Assert.ThrowsAny<PantsException>(() =>
            LeveledCompactionPlanner.Pick(
                [invalid],
                columnFamilyId: 0,
                Configuration(),
                snapshotHorizon: null,
                force: false));

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public void ShouldProduceTheSamePlanForTheSameMetadataSnapshot()
    {
        MidgeFileMeta[] files =
        [
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l1-1.sst", 1, 3, "b", "e")
        ];
        PantsCompactionConfiguration configuration = Configuration(l0FileCountTrigger: 2);

        CompactionPlan? first = LeveledCompactionPlanner.Pick(
            files, 0, configuration, snapshotHorizon: 5, force: false);
        CompactionPlan? second = LeveledCompactionPlanner.Pick(
            files, 0, configuration, snapshotHorizon: 5, force: false);

        Assert.Equal(first?.SourceLevel, second?.SourceLevel);
        Assert.Equal(first?.TargetLevel, second?.TargetLevel);
        Assert.Equal(
            first?.Inputs.Select(static file => file.Name),
            second?.Inputs.Select(static file => file.Name));
    }

    private static PantsCompactionConfiguration Configuration(
        long l0SizeTriggerBytes = 4L * 1024 * 1024,
        int l0FileCountTrigger = 4,
        int maximumInputFiles = 64,
        int levelMultiplier = 10,
        long l1TargetSizeBytes = 40L * 1024 * 1024) => new(
            l0SizeTriggerBytes,
            l0FileCountTrigger,
            maximumInputFiles,
            levelMultiplier,
            l1TargetSizeBytes,
            MaximumLevels: 7);

    private static MidgeFileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest,
        ulong sizeBytes = 1024) => new()
        {
            Name = name,
            Level = level,
            ColumnFamilyId = 0,
            SstSequence = sequence,
            SizeBytes = sizeBytes,
            SmallestKey = TestBytes.FromString(smallest).Select(static value => (int)value).ToArray(),
            LargestKey = TestBytes.FromString(largest).Select(static value => (int)value).ToArray()
        };
}
