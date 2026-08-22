namespace Pants.Tests;

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

    private static MidgeSstEntry Entry(string key, ulong sequence) => new(
        TestBytes.FromString(key),
        TestBytes.FromString("v"),
        sequence,
        Expiration: null,
        IsDelete: false);
}
