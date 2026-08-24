namespace Cntryl.Pants.Tests.Storage;

public sealed class CompactionMergerTests
{
    [Fact]
    public void ShouldDropEligiblePointTombstoneAndItsOlderValueWithoutASnapshot()
    {
        var result = CompactionMerger.Merge(
            [Contents(Entry("key", "value", 1), Entry("key", null, 2, true))],
            Plan(true, true, null));

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ShouldRetainVersionsNeededByTheOldestSnapshot()
    {
        var result = CompactionMerger.Merge(
            [Contents(Entry("key", "old", 1), Entry("key", "new", 3))],
            Plan(true, true, 1));

        Assert.Equal([3UL, 1UL], result.Entries.Select(static entry => entry.Sequence));
    }

    [Fact]
    public void ShouldDropEligibleRangeTombstoneAndCoveredOlderValues()
    {
        var contents = new SstContents(
            [Entry("b", "value", 1), Entry("z", "kept", 1)],
            [new RangeTombstone(TestBytes.FromString("a"), TestBytes.FromString("c"), 2)],
            1);

        var result = CompactionMerger.Merge(
            [contents],
            Plan(true, true, null));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("z", TestBytes.ToText(entry.Key));
        Assert.Empty(result.RangeTombstones);
    }

    [Fact]
    public void ShouldRetainRangeTombstoneWithoutWholeFamilyCoverage()
    {
        var contents = new SstContents(
            [],
            [new RangeTombstone(TestBytes.FromString("a"), TestBytes.FromString("c"), 2)],
            1);

        var result = CompactionMerger.Merge(
            [contents],
            Plan(true, false, null));

        Assert.Single(result.RangeTombstones);
    }

    [Fact]
    public void ShouldRetainDeleteVisibleAtSnapshotHorizon()
    {
        var result = CompactionMerger.Merge(
            [Contents(
                Entry("key", "old", 1),
                Entry("key", null, 5, true),
                Entry("key", "new", 10))],
            Plan(true, true, 7));

        Assert.Equal([10UL, 5UL], result.Entries.Select(static entry => entry.Sequence));
        Assert.True(result.Entries[1].IsDelete);
    }

    [Fact]
    public void ShouldMergeVersionsAndRangeTombstonesAcrossFiles()
    {
        var result = CompactionMerger.Merge(
            [
                Contents(Entry("key", "old", 2)),
                new SstContents([], [new RangeTombstone(
                    TestBytes.FromString("a"), TestBytes.FromString("z"), 5)], 1),
                Contents(Entry("key", "new", 10))
            ],
            Plan(true, true, null));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(10UL, entry.Sequence);
        Assert.Equal("new", TestBytes.ToText(entry.Value!));
    }

    static SstContents Contents(params SstEntry[] entries) => new(entries, [], 1);

    static SstEntry Entry(
        string key,
        string? value,
        ulong sequence,
        bool isDelete = false) => new(
        TestBytes.FromString(key),
        value is null ? null : TestBytes.FromString(value),
        sequence,
        null,
        isDelete);

    static CompactionPlan Plan(bool pointEligible, bool rangeEligible, long? horizon) => new(
        0,
        1,
        0,
        horizon,
        pointEligible,
        rangeEligible,
        []);
}
