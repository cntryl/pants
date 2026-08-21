namespace Pants.Tests;

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
            l0FileTrigger: 3,
            l1TargetBytes: 40L * 1024 * 1024,
            maximumInputs: 5,
            force: false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.TargetLevel);
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

        CompactionPlan? plan = LeveledCompactionPlanner.Pick(
            files,
            columnFamilyId: 0,
            l0FileTrigger: 2,
            l1TargetBytes: 40L * 1024 * 1024,
            maximumInputs: 4,
            force: false);

        Assert.Null(plan);
    }

    private static MidgeFileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest) => new()
        {
            Name = name,
            Level = level,
            ColumnFamilyId = 0,
            SstSequence = sequence,
            SizeBytes = 1024,
            SmallestKey = TestBytes.FromString(smallest).Select(static value => (int)value).ToArray(),
            LargestKey = TestBytes.FromString(largest).Select(static value => (int)value).ToArray()
        };
}
