using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsCompactionKeyGroupParityTests
{
    [Fact]
    public void ShouldSelectHighestSequenceWhenKeyGroupHasSeveralVersions()
    {
        var results = MergeBoth(
            [Contents(Entry("k", "old", 3), Entry("k", "new", 9), Entry("k", "mid", 5))],
            Plan());

        AssertEntries(results, ("k", "new", 9, false));
    }

    [Fact]
    public void ShouldKeepOneVersionWhenEqualSequenceVersionsAreIdentical()
    {
        var duplicate = Contents(Entry("k", "same", 4));
        var results = MergeBoth([duplicate, duplicate], Plan());

        AssertEntries(results, ("k", "same", 4, false));
    }

    [Fact]
    public void ShouldRejectKeyGroupWhenEqualSequenceVersionsDisagree()
    {
        var contents = new[]
        {
            Contents(Entry("k", "left", 4)),
            Contents(Entry("k", "right", 4))
        };

        AssertCorruptBoth(contents, Plan());
    }

    [Fact]
    public void ShouldRejectShadowedEqualSequenceConflictDuringCompaction()
    {
        var contents = new[]
        {
            Contents(Entry("k", "newest", 10)),
            Contents(Entry("k", "left", 5)),
            Contents(Entry("k", "right", 5))
        };

        AssertCorruptBoth(contents, Plan());
    }

    [Fact]
    public void ShouldKeepNewestVersionWhenShadowedEqualSequenceCopiesAreIdentical()
    {
        var duplicate = Contents(Entry("k", "same", 5));
        var results = MergeBoth(
            [Contents(Entry("k", "newest", 10)), duplicate, duplicate],
            Plan());

        AssertEntries(results, ("k", "newest", 10, false));
    }

    [Fact]
    public void ShouldSelectNewestVersionWhenChainedFilesYieldOlderVersionFirst()
    {
        var results = MergeBoth(
            [
                Contents(Entry("k", "source", 3)),
                Contents(Entry("k", "left-target", 1)),
                Contents(Entry("k", "right-target", 5))
            ],
            Plan());

        AssertEntries(results, ("k", "right-target", 5, false));
    }

    [Fact]
    public void ShouldRejectShadowedEqualSequenceConflictWhenVersionsArriveUnordered()
    {
        var contents = new[]
        {
            Contents(Entry("k", "left", 5)),
            Contents(Entry("k", "newest", 10)),
            Contents(Entry("k", "right", 5))
        };

        AssertCorruptBoth(contents, Plan());
    }

    [Fact]
    public void ShouldSelectNothingWhenKeyGroupHoldsOnlyRangeEvents()
    {
        var results = MergeBoth(
            [Contents([], [Range("a", "m", 7)])],
            Plan(horizon: 5));

        AssertEntries(results);
        AssertRanges(results, ("a", "m", 7));
    }

    [Fact]
    public void ShouldDropPointTombstoneWhenItIsAtOrBelowTheHorizon()
    {
        var results = MergeBoth(
            [Contents(Entry("k", null, 5, isDelete: true))],
            Plan(horizon: 5));

        AssertEntries(results);
    }

    [Fact]
    public void ShouldKeepPointTombstoneWhenASnapshotCanStillSeeIt()
    {
        var results = MergeBoth(
            [Contents(Entry("k", null, 6, isDelete: true))],
            Plan(horizon: 5));

        AssertEntries(results, ("k", null, 6, true));
    }

    [Fact]
    public void ShouldKeepPointTombstoneWhenPointGcIsNotEligible()
    {
        var results = MergeBoth(
            [Contents(Entry("k", null, 1, isDelete: true))],
            Plan(horizon: 100, pointGcEligible: false));

        AssertEntries(results, ("k", null, 1, true));
    }

    [Fact]
    public void ShouldDropValueWhenAnObsoleteActiveCoverIsAtLeastAsNew()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("a", "z", 5)])],
            Plan(horizon: 10));

        AssertEntries(results);
        AssertRanges(results);
    }

    [Fact]
    public void ShouldKeepValueWhenTheActiveCoverIsOlderThanTheValue()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 6)], [Range("a", "z", 5)])],
            Plan(horizon: 10));

        AssertEntries(results, ("k", "value", 6, false));
        AssertRanges(results);
    }

    [Fact]
    public void ShouldKeepValueWhenTheActiveCoverDoesNotContainTheKey()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("m", "z", 9)])],
            Plan(horizon: 10));

        AssertEntries(results, ("k", "value", 5, false));
        AssertRanges(results);
    }

    [Fact]
    public void ShouldKeepValueWhenRangeGcIsNotEligibleEvenIfTheCoverIsNewer()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("a", "z", 8)])],
            Plan(horizon: 10, rangeGcEligible: false));

        AssertEntries(results, ("k", "value", 5, false));
        AssertRanges(results, ("a", "z", 8));
    }

    [Fact]
    public void ShouldKeepValueWhenRangeEndsAtThePointKey()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("a", "k", 8)])],
            Plan(horizon: 10));

        AssertEntries(results, ("k", "value", 5, false));
        AssertRanges(results);
    }

    [Fact]
    public void ShouldDropValueWhenAnObsoleteRangeStartInTheGroupCoversIt()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("a", "z", 8)])],
            Plan(horizon: 10));

        AssertEntries(results);
        AssertRanges(results);
    }

    [Fact]
    public void ShouldKeepValueWhenTheCoveringRangeStartIsNotYetObsolete()
    {
        var results = MergeBoth(
            [Contents([Entry("k", "value", 5)], [Range("a", "z", 11)])],
            Plan(horizon: 10));

        AssertEntries(results, ("k", "value", 5, false));
        AssertRanges(results, ("a", "z", 11));
    }

    [Fact]
    public void ShouldKeepValueWhenNoTombstoneCoversIt()
    {
        var results = MergeBoth(
            [Contents(Entry("k", "value", 5))],
            Plan(horizon: 10));

        AssertEntries(results, ("k", "value", 5, false));
        AssertRanges(results);
    }

    static void AssertCorruptBoth(SstContents[] contents, CompactionPlan plan)
    {
        Assert.Throws<PantsCorruptionException>(() => CompactionMerger.Merge(contents, plan));
        Assert.Throws<PantsCorruptionException>(() => MergeStreaming(contents, plan));
    }

    static CompactionMergeResult[] MergeBoth(SstContents[] contents, CompactionPlan plan)
    {
        var legacy = CompactionMerger.Merge(contents, plan);
        var streaming = MergeStreaming(contents, plan);
        Assert.Equal(Describe(legacy.Entries), Describe(streaming.Entries));
        Assert.Equal(DescribeRanges(legacy.RangeTombstones), DescribeRanges(streaming.RangeTombstones));
        return [legacy, streaming];
    }

    static CompactionMergeResult MergeStreaming(SstContents[] contents, CompactionPlan plan)
    {
        using var directory = new TemporaryDirectory();
        var readers = contents
            .Select((content, index) => OpenReader(directory.Path, $"input-{index}.sst", content))
            .ToArray();
        try
        {
            var partitions = StreamingCompactionMerger.MergeAndPartition(
                    readers,
                    plan,
                    1024 * 1024)
                .ToArray();
            return new CompactionMergeResult(
                partitions.SelectMany(static partition => partition.Entries).ToArray(),
                partitions.SelectMany(static partition => partition.RangeTombstones).ToArray());
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    static SstReader OpenReader(string directory, string fileName, SstContents contents)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(
            path,
            SstCodec.Encode(
                contents.Entries.ToArray(),
                contents.RangeTombstones.ToArray(),
                PantsPerformanceGoal.Latency));
        return SstReader.Open(path);
    }

    static SstContents Contents(params SstEntry[] entries) => new(entries, [], 1);

    static SstContents Contents(SstEntry[] entries, RangeTombstone[] ranges) => new(entries, ranges, 1);

    static SstEntry Entry(string key, string? value, ulong sequence, bool isDelete = false) =>
        new(
            TestBytes.FromString(key),
            value is null ? null : TestBytes.FromString(value),
            sequence,
            null,
            isDelete);

    static RangeTombstone Range(string start, string end, ulong sequence) => new(
        TestBytes.FromString(start),
        TestBytes.FromString(end),
        sequence);

    static CompactionPlan Plan(
        long? horizon = null,
        bool pointGcEligible = true,
        bool rangeGcEligible = true) => new(
        0,
        1,
        0,
        horizon,
        pointGcEligible,
        rangeGcEligible,
        []);

    static void AssertEntries(
        IReadOnlyList<CompactionMergeResult> results,
        params (string Key, string? Value, ulong Sequence, bool IsDelete)[] expected)
    {
        foreach (var result in results)
        {
            Assert.Equal(expected, Describe(result.Entries));
        }
    }

    static void AssertRanges(
        IReadOnlyList<CompactionMergeResult> results,
        params (string Start, string End, ulong Sequence)[] expected)
    {
        foreach (var result in results)
        {
            Assert.Equal(expected, DescribeRanges(result.RangeTombstones));
        }
    }

    static (string Key, string? Value, ulong Sequence, bool IsDelete)[] Describe(
        IEnumerable<SstEntry> entries) => entries
        .Select(static entry => (
            TestBytes.ToText(entry.Key),
            entry.Value is null ? null : TestBytes.ToText(entry.Value),
            entry.Sequence,
            entry.IsDelete))
        .ToArray();

    static (string Start, string End, ulong Sequence)[] DescribeRanges(
        IEnumerable<RangeTombstone> ranges) => ranges
        .Select(static range => (
            TestBytes.ToText(range.Start),
            TestBytes.ToText(range.End),
            range.Sequence))
        .ToArray();
}
