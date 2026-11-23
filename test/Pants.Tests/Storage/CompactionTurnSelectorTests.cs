using Cntryl.Pants.Storage.Internal.Compaction.Compaction;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class CompactionTurnSelectorTests
{
    static readonly IReadOnlySet<uint> NoFamilies = new HashSet<uint>();

    [Fact]
    public void ShouldTargetOverfullL1GivenL0AtTriggerButNotCritical()
    {
        // Arrange
        FileMeta[] files =
        [
            File("l0-a.sst", 0, 1, "a", "b"),
            File("l0-b.sst", 0, 2, "c", "d"),
            File("l1-overfull.sst", 1, 3, "e", "f", sizeBytes: 101)
        ];

        // Act
        var plan = Select(files, [0], NoFamilies, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
        Assert.Equal(2u, plan.TargetLevel);
    }

    [Fact]
    public void ShouldTargetL0GivenL0AtTheCriticalCeilingAndOverfullL1()
    {
        // Arrange
        FileMeta[] files =
        [
            File("l0-a.sst", 0, 1, "a", "b"),
            File("l0-b.sst", 0, 2, "c", "d"),
            File("l1-overfull.sst", 1, 3, "e", "f", sizeBytes: 101)
        ];

        // Act
        var plan = Select(files, [0], new HashSet<uint> { 0 }, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(0u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldFallBackToSoftL0GivenNoInnerLevelIsOverfull()
    {
        // Arrange
        FileMeta[] files =
        [
            File("l0-a.sst", 0, 1, "a", "b"),
            File("l0-b.sst", 0, 2, "c", "d")
        ];

        // Act
        var plan = Select(files, [0], NoFamilies, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(0u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldPickTheDeepestOverfullLevelAcrossFamilies()
    {
        // Arrange
        FileMeta[] files =
        [
            File("f0-l1.sst", 1, 1, "a", "b", sizeBytes: 101, columnFamilyId: 0),
            File("f1-l2.sst", 2, 2, "a", "b", sizeBytes: 1001, columnFamilyId: 1)
        ];

        // Act
        var plan = Select(files, [0, 1], NoFamilies, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(1u, plan.ColumnFamilyId);
        Assert.Equal(2u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldAlternateCriticalFamiliesAcrossTurns()
    {
        // Arrange
        var files = FamiliesWithL0Files([0, 1], filesPerFamily: 2);
        var critical = new HashSet<uint> { 0, 1 };
        uint? cursor = null;
        var scheduled = new List<uint>();

        // Act
        for (var turn = 0; turn < 4; turn++)
        {
            var plan = Select(files, [0, 1], critical, cursor);
            Assert.NotNull(plan);
            scheduled.Add(plan.ColumnFamilyId);
            cursor = plan.ColumnFamilyId;
        }

        // Assert
        Assert.Equal([0u, 1u, 0u, 1u], scheduled);
    }

    [Fact]
    public void ShouldRotateSoftL0AcrossFamiliesGivenNoCriticalFamily()
    {
        // Arrange
        var files = FamiliesWithL0Files([0, 1, 2], filesPerFamily: 2);
        uint? cursor = null;
        var scheduled = new List<uint>();

        // Act
        for (var turn = 0; turn < 3; turn++)
        {
            var plan = Select(files, [0, 1, 2], NoFamilies, cursor);
            Assert.NotNull(plan);
            scheduled.Add(plan.ColumnFamilyId);
            cursor = plan.ColumnFamilyId;
        }

        // Assert
        Assert.Equal([0u, 1u, 2u], scheduled);
    }

    [Fact]
    public void ShouldSkipExcludedFamilyGivenItsTurnFailed()
    {
        // Arrange
        var files = FamiliesWithL0Files([0, 1], filesPerFamily: 2);
        var critical = new HashSet<uint> { 0, 1 };

        // Act
        var plan = Select(files, [0, 1], critical, null, new HashSet<uint> { 0 });

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(1u, plan.ColumnFamilyId);
    }

    [Fact]
    public void ShouldReturnNoPlanGivenEveryFamilyWithWorkIsExcluded()
    {
        // Arrange
        var files = FamiliesWithL0Files([0], filesPerFamily: 2);

        // Act
        var plan = Select(files, [0], NoFamilies, null, new HashSet<uint> { 0 });

        // Assert
        Assert.Null(plan);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    public void ShouldBoundTurnWorkToOneFamilyPlanGivenAnyFamilyCount(int familyCount)
    {
        // Arrange
        var familyIds = Enumerable.Range(0, familyCount).Select(static id => (uint)id).ToArray();
        var files = FamiliesWithL0Files(familyIds, filesPerFamily: 3);
        var critical = familyIds.ToHashSet();

        // Act
        var plan = Select(files, familyIds, critical, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Inputs.Count);
        Assert.All(plan.Inputs, input => Assert.Equal(plan.ColumnFamilyId, input.ColumnFamilyId));
    }

    static CompactionPlan? Select(
        FileMeta[] files,
        uint[] familyIds,
        IReadOnlySet<uint> critical,
        uint? cursor,
        IReadOnlySet<uint>? excluded = null
    ) =>
        CompactionTurnSelector.Select(
            files,
            familyIds,
            critical,
            cursor,
            new PantsCompactionConfiguration(
                L0SizeTriggerBytes: 4L * 1024 * 1024,
                L0FileCountTrigger: 2,
                MaximumInputFiles: 64,
                LevelMultiplier: 10,
                L1TargetSizeBytes: 100,
                MaximumLevels: 6),
            null,
            null,
            CompactionTurnMode.Ordinary,
            excluded);

    static FileMeta[] FamiliesWithL0Files(uint[] familyIds, int filesPerFamily) =>
        familyIds
            .SelectMany(familyId => Enumerable.Range(0, filesPerFamily).Select(index =>
                File(
                    $"f{familyId}-l0-{index}.sst",
                    0,
                    (ulong)index + 1,
                    $"{(char)('a' + index * 2)}",
                    $"{(char)('b' + index * 2)}",
                    columnFamilyId: familyId)))
            .ToArray();

    static FileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest,
        ulong sizeBytes = 10,
        uint columnFamilyId = 0
    ) => new()
    {
        Name = name,
        Level = level,
        ColumnFamilyId = columnFamilyId,
        SstSequence = sequence,
        SizeBytes = sizeBytes,
        SmallestKey = TestBytes.FromString(smallest).ToArray(),
        LargestKey = TestBytes.FromString(largest).ToArray(),
        KeyBoundsComplete = true
    };
}
