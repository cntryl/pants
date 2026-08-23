namespace Cntryl.Pants.Tests;

public sealed class CompactionMergerTests
{
    [Fact]
    public void ShouldDropEligiblePointTombstoneAndItsOlderValueWithoutASnapshot()
    {
        CompactionMergeResult result = CompactionMerger.Merge(
            [Contents(Entry("key", "value", 1), Entry("key", null, 2, isDelete: true))],
            Plan(pointEligible: true, rangeEligible: true, horizon: null));

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ShouldRetainVersionsNeededByTheOldestSnapshot()
    {
        CompactionMergeResult result = CompactionMerger.Merge(
            [Contents(Entry("key", "old", 1), Entry("key", "new", 3))],
            Plan(pointEligible: true, rangeEligible: true, horizon: 1));

        Assert.Equal([3UL, 1UL], result.Entries.Select(static entry => entry.Sequence));
    }

    [Fact]
    public void ShouldDropEligibleRangeTombstoneAndCoveredOlderValues()
    {
        var contents = new MidgeSstContents(
            [Entry("b", "value", 1), Entry("z", "kept", 1)],
            [new MidgeRangeTombstone(TestBytes.FromString("a"), TestBytes.FromString("c"), 2)],
            DataBlockCount: 1);

        CompactionMergeResult result = CompactionMerger.Merge(
            [contents],
            Plan(pointEligible: true, rangeEligible: true, horizon: null));

        MidgeSstEntry entry = Assert.Single(result.Entries);
        Assert.Equal("z", TestBytes.ToText(entry.Key));
        Assert.Empty(result.RangeTombstones);
    }

    [Fact]
    public void ShouldRetainRangeTombstoneWithoutWholeFamilyCoverage()
    {
        var contents = new MidgeSstContents(
            [],
            [new MidgeRangeTombstone(TestBytes.FromString("a"), TestBytes.FromString("c"), 2)],
            DataBlockCount: 1);

        CompactionMergeResult result = CompactionMerger.Merge(
            [contents],
            Plan(pointEligible: true, rangeEligible: false, horizon: null));

        Assert.Single(result.RangeTombstones);
    }

    private static MidgeSstContents Contents(params MidgeSstEntry[] entries) => new(entries, [], 1);

    private static MidgeSstEntry Entry(
        string key,
        string? value,
        ulong sequence,
        bool isDelete = false) => new(
            TestBytes.FromString(key),
            value is null ? null : TestBytes.FromString(value),
            sequence,
            Expiration: null,
            isDelete);

    private static CompactionPlan Plan(bool pointEligible, bool rangeEligible, long? horizon) => new(
        SourceLevel: 0,
        TargetLevel: 1,
        ColumnFamilyId: 0,
        SnapshotHorizon: horizon,
        PointTombstoneGcEligible: pointEligible,
        RangeTombstoneGcEligible: rangeEligible,
        Inputs: []);
}
