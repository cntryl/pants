using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class LeveledCompactionPlannerTests
{
    [Fact]
    public void ShouldPickBoundedL0InputsAndEveryOverlappingL1File()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-3.sst", 0, 3, "g", "i"),
            File("l1-1.sst", 1, 4, "b", "e"),
            File("l1-2.sst", 1, 5, "e", "h"),
            File("l1-unrelated.sst", 1, 6, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, maximumInputFiles: 5),
            42,
            false);

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
    public void ShouldSkipFamilyWhenOverlapClosureExceedsInputLimit()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "f"),
            File("l1-2.sst", 1, 4, "g", "m"),
            File("l1-3.sst", 1, 5, "n", "z")
        ];

        var poisoned = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 2, maximumInputFiles: 4),
            null,
            false);
        var healthy = LeveledCompactionPlanner.Pick(
            [
                File("healthy-1.sst", 0, 1, "a", "b", columnFamilyId: 1),
                File("healthy-2.sst", 0, 2, "c", "d", columnFamilyId: 1)
            ],
            1,
            Configuration(l0FileCountTrigger: 2, maximumInputFiles: 4),
            null,
            false);

        Assert.Null(poisoned);
        Assert.NotNull(healthy);
        Assert.Equal(2, healthy.Inputs.Count);
    }

    [Fact]
    public void ShouldAbsorbTransitivelyOverlappingTargetFiles()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "b", "c"),
            File("l1-a.sst", 1, 2, "c", "d"),
            File("l1-b.sst", 1, 3, "d", "e"),
            File("l1-unrelated.sst", 1, 4, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files, 0, Configuration(l0FileCountTrigger: 1), null, false);

        Assert.NotNull(plan);
        Assert.Equal(["l0-1.sst", "l1-a.sst", "l1-b.sst"],
            plan.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldForceL0CompactionBelowNormalTriggers()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "b"),
            File("l0-2.sst", 0, 2, "c", "d")
        ];

        var plan = LeveledCompactionPlanner.Pick(files, 0, Configuration(), null, true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Inputs.Count);
    }

    [Fact]
    public void ShouldNotForceSingleL0File()
    {
        var plan = LeveledCompactionPlanner.Pick(
            [File("l0-1.sst", 0, 1, "a", "b")], 0, Configuration(), null, true);

        Assert.Null(plan);
    }

    [Fact]
    public void ShouldForceInnerLevelCompactionBelowNormalTrigger()
    {
        FileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "b"),
            File("l1-2.sst", 1, 2, "c", "d")
        ];

        var plan = LeveledCompactionPlanner.Pick(files, 0, Configuration(), null, true);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldRespectInputLimitWhenForced()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "m")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files, 0, Configuration(maximumInputFiles: 2), null, true);

        Assert.Null(plan);
    }

    [Fact]
    public void ShouldTriggerL0CompactionByBytesIndependentlyOfFileCount()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z", 1025)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(1024, 10),
            null,
            false);

        Assert.NotNull(plan);
    }

    [Fact]
    public void ShouldTriggerInnerLevelByConfiguredSizeRatio()
    {
        FileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "m", 1025),
            File("l2-1.sst", 2, 2, "n", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l1TargetSizeBytes: 1024, levelMultiplier: 3),
            null,
            false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
        Assert.Equal(2u, plan.TargetLevel);
    }

    [Fact]
    public void ShouldMakePointAndRangeTombstoneEligibilityDistinct()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-unselected.sst", 0, 2, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 1, maximumInputFiles: 1),
            7,
            false);

        Assert.NotNull(plan);
        Assert.True(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
    }

    [Fact]
    public void ShouldRejectInvalidFileMetadataAsCorruption()
    {
        var invalid = File("invalid.sst", 7, 1, "z", "a");

        var exception = Assert.ThrowsAny<PantsException>(() =>
            LeveledCompactionPlanner.Pick(
                [invalid],
                0,
                Configuration(),
                null,
                false));

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public void ShouldProduceTheSamePlanForTheSameMetadataSnapshot()
    {
        FileMeta[] files =
        [
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l1-1.sst", 1, 3, "b", "e")
        ];
        var configuration = Configuration(l0FileCountTrigger: 2);

        var first = LeveledCompactionPlanner.Pick(
            files, 0, configuration, 5, false);
        var second = LeveledCompactionPlanner.Pick(
            files, 0, configuration, 5, false);

        Assert.Equal(first?.SourceLevel, second?.SourceLevel);
        Assert.Equal(first?.TargetLevel, second?.TargetLevel);
        Assert.Equal(
            first?.Inputs.Select(static file => file.Name),
            second?.Inputs.Select(static file => file.Name));
    }

    static PantsCompactionConfiguration Configuration(
        long l0SizeTriggerBytes = 4L * 1024 * 1024,
        int l0FileCountTrigger = 4,
        int maximumInputFiles = 64,
        int levelMultiplier = 10,
        long l1TargetSizeBytes = 40L * 1024 * 1024) => new(
        l0SizeTriggerBytes,
        l0FileCountTrigger,
        maximumInputFiles,
        levelMultiplier,
        l1TargetSizeBytes);

    static FileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest,
        ulong sizeBytes = 1024,
        uint columnFamilyId = 0) => new()
        {
            Name = name,
            Level = level,
            ColumnFamilyId = columnFamilyId,
            SstSequence = sequence,
            SizeBytes = sizeBytes,
            SmallestKey = TestBytes.FromString(smallest).Select(static value => (int)value).ToArray(),
            LargestKey = TestBytes.FromString(largest).Select(static value => (int)value).ToArray()
        };
}
