namespace Cntryl.Pants.Tests;

public sealed class CompactionOutputPartitionerTests
{
    [Fact]
    public void ShouldPartitionEntriesAtTheConfiguredTargetWithoutSplittingAKey()
    {
        var merged = new CompactionMergeResult(
            [Entry("a", 1), Entry("b", 2), Entry("c", 3)],
            []);

        IReadOnlyList<CompactionMergeResult> partitions =
            CompactionOutputPartitioner.Partition(merged, targetSizeBytes: 34);

        Assert.Equal(3, partitions.Count);
        Assert.Equal(["a", "b", "c"], partitions
            .SelectMany(static partition => partition.Entries)
            .Select(static entry => TestBytes.ToText(entry.Key)));
    }

    [Fact]
    public void ShouldClipRangeTombstonesAtNonOverlappingOutputBoundaries()
    {
        var merged = new CompactionMergeResult(
            [Entry("a", 1), Entry("b", 2), Entry("c", 3)],
            [new MidgeRangeTombstone(
                TestBytes.FromString("0"),
                TestBytes.FromString("z"),
                Sequence: 4)]);

        IReadOnlyList<CompactionMergeResult> partitions =
            CompactionOutputPartitioner.Partition(merged, targetSizeBytes: 34);

        Assert.Equal(3, partitions.Count);
        Assert.Equal("b", TestBytes.ToText(Assert.Single(partitions[0].RangeTombstones).End));
        Assert.Equal("b", TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).Start));
        Assert.Equal("c", TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).End));
        Assert.Equal("c", TestBytes.ToText(Assert.Single(partitions[2].RangeTombstones).Start));
    }

    private static MidgeSstEntry Entry(string key, ulong sequence) => new(
        TestBytes.FromString(key),
        TestBytes.FromString("v"),
        sequence,
        Expiration: null,
        IsDelete: false);
}
