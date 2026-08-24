namespace Cntryl.Pants.Tests.Storage;

public sealed class CompactionOutputPartitionerTests
{
    [Fact]
    public void ShouldPartitionEntriesAtTheConfiguredTargetWithoutSplittingAKey()
    {
        var merged = new CompactionMergeResult(
            [Entry("a", 1), Entry("b", 2), Entry("c", 3)],
            []);

        var partitions =
            CompactionOutputPartitioner.Partition(merged, 34);

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
            [
                new RangeTombstone(
                    TestBytes.FromString("0"),
                    TestBytes.FromString("z"),
                    4)
            ]);

        var partitions =
            CompactionOutputPartitioner.Partition(merged, 34);

        Assert.Equal(3, partitions.Count);
        Assert.Equal("b", TestBytes.ToText(Assert.Single(partitions[0].RangeTombstones).End));
        Assert.Equal("b", TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).Start));
        Assert.Equal("c", TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).End));
        Assert.Equal("c", TestBytes.ToText(Assert.Single(partitions[2].RangeTombstones).Start));
    }

    [Fact]
    public void ShouldKeepAnOversizedMultiVersionKeyTogetherAndResetTheNextPartitionBudget()
    {
        var merged = new CompactionMergeResult(
            [
                Entry("k", 3, 20),
                Entry("k", 2, 20),
                Entry("k", 1, 20),
                Entry("m", 4, 1),
                Entry("n", 5, 1)
            ],
            []);

        var partitions = CompactionOutputPartitioner.Partition(merged, 80);

        Assert.Equal(2, partitions.Count);
        Assert.Equal([3UL, 2UL, 1UL], partitions[0].Entries.Select(static entry => entry.Sequence));
        Assert.All(partitions[0].Entries, entry =>
            Assert.Equal("k", TestBytes.ToText(entry.Key)));
        Assert.Equal(["m", "n"], partitions[1].Entries.Select(static entry => TestBytes.ToText(entry.Key)));
        Assert.All(partitions.Zip(partitions.Skip(1)), pair =>
        {
            var leftKeys = pair.First.Entries.Select(static entry => entry.Key);
            var rightKeys = pair.Second.Entries.Select(static entry => entry.Key);
            Assert.Empty(leftKeys.Intersect(rightKeys, ByteArrayComparer.Instance));
        });
    }

    [Fact]
    public void ShouldPartitionRangeTombstoneOnlyOutputAtTheConfiguredTarget()
    {
        var merged = new CompactionMergeResult(
            [],
            [
                Range("a", "b", 3),
                Range("c", "d", 2),
                Range("e", "f", 1)
            ]);

        var partitions = CompactionOutputPartitioner.Partition(merged, 20);

        Assert.Equal(3, partitions.Count);
        Assert.All(partitions, partition =>
        {
            Assert.Empty(partition.Entries);
            Assert.InRange(EstimatedRangeBytes(partition.RangeTombstones), 1, 20);
        });
        Assert.Equal([3UL, 2UL, 1UL], partitions
            .SelectMany(static partition => partition.RangeTombstones)
            .Select(static range => range.Sequence));
    }

    [Fact]
    public void ShouldKeepOneOversizedRangeTombstoneInOnePartition()
    {
        var range = Range(new string('a', 20), new string('z', 20), 1);

        var partition = Assert.Single(CompactionOutputPartitioner.Partition(
            new CompactionMergeResult([], [range]),
            20));

        Assert.Same(range, Assert.Single(partition.RangeTombstones));
    }

    [Fact]
    public void ShouldReturnNoPartitionsForEmptyMergedOutput()
    {
        var partitions = CompactionOutputPartitioner.Partition(
            new CompactionMergeResult([], []),
            1);

        Assert.Empty(partitions);
    }

    static SstEntry Entry(string key, ulong sequence, int valueSize = 1) => new(
        TestBytes.FromString(key),
        new byte[valueSize],
        sequence,
        null,
        false);

    static RangeTombstone Range(string start, string end, ulong sequence) => new(
        TestBytes.FromString(start),
        TestBytes.FromString(end),
        sequence);

    static long EstimatedRangeBytes(IReadOnlyList<RangeTombstone> ranges) => ranges.Sum(
        static range => range.Start.Length + range.End.Length + 16L);
}
