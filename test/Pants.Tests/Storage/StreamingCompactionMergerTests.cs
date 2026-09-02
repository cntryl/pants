namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 5 (issue #219), the merge/partition side: <see cref="StreamingCompactionMerger"/> does
/// an incremental k-way merge over per-input <see cref="SstBlockIterator"/>s and emits completed
/// output partitions as it goes, instead of <c>CompactionMerger.Merge</c> materializing every
/// input's entries into one array before <c>CompactionOutputPartitioner.Partition</c> slices it.
/// Every test here is an oracle-equivalence check against that existing (unchanged, still used
/// elsewhere) pipeline, over the same input <see cref="SstContents"/> — same entries, same
/// range-tombstone masking/retention/GC-eligibility rules, same partition boundaries — since
/// that logic is durability-critical and was deliberately not reimplemented, only re-driven
/// incrementally.
/// </summary>
public sealed class StreamingCompactionMergerTests
{
    [Fact]
    public void ShouldMatchTheExistingMergerGivenNonOverlappingInputs()
    {
        AssertEquivalent(
            [Build(("a", 1, "1"), ("b", 2, "2")), Build(("c", 3, "3"), ("d", 4, "4"))],
            Plan(),
            targetSizeBytes: 1024 * 1024);
    }

    [Fact]
    public void ShouldPreferTheNewestVersionGivenTheSameKeyInMultipleInputs()
    {
        AssertEquivalent(
            [Build(("key", 1, "old")), Build(("key", 5, "new"))],
            Plan(),
            targetSizeBytes: 1024 * 1024);
    }

    [Fact]
    public void ShouldDropAPointTombstoneOnlyOnceItIsGcEligibleBelowTheHorizon()
    {
        var files = new[] { Build(("key", 1, null), ("other", 2, "value")) };
        AssertEquivalent(files, Plan(snapshotHorizon: 10, pointTombstoneGcEligible: true), 1024 * 1024);
        AssertEquivalent(files, Plan(snapshotHorizon: 0, pointTombstoneGcEligible: true), 1024 * 1024);
        AssertEquivalent(files, Plan(snapshotHorizon: 10, pointTombstoneGcEligible: false), 1024 * 1024);
    }

    [Fact]
    public void ShouldRetainOnlyOneOlderVersionAtOrBelowTheSnapshotHorizon()
    {
        // Same key across three separate input files (mirroring real overlapping generations —
        // a single SST never contains duplicate keys).
        var files = new[] { Build(("key", 1, "v1")), Build(("key", 5, "v5")), Build(("key", 9, "v9")) };
        AssertEquivalent(files, Plan(snapshotHorizon: 5), 1024 * 1024);
    }

    [Fact]
    public void ShouldMaskEntriesCoveredByAGcEligibleRangeTombstone()
    {
        var file = Build([("a", 1, "a"), ("b", 2, "b"), ("c", 3, "c")], [("a", "c", 10)]);
        AssertEquivalent([file], Plan(snapshotHorizon: 20, rangeTombstoneGcEligible: true), 1024 * 1024);
        AssertEquivalent([file], Plan(snapshotHorizon: 20, rangeTombstoneGcEligible: false), 1024 * 1024);
    }

    [Fact]
    public void ShouldProduceMultipleOutputPartitionsGivenATightSizeBudgetAndClampTombstonesToEach()
    {
        var entries = Enumerable.Range(0, 40)
            .Select(index => ($"key-{index:D4}", (ulong)(index + 1), (string?)new string('v', 200)))
            .ToArray();
        var file = Build(entries, [("key-0005", "key-0035", 100)]);

        AssertEquivalent([file], Plan(), targetSizeBytes: 2048);
    }

    [Fact]
    public void ShouldMergeManySmallInputsAcrossMultipleOverlappingKeyRanges()
    {
        var files = Enumerable.Range(0, 6)
            .Select(fileIndex => Build(Enumerable.Range(0, 20)
                .Select(index => (
                    $"key-{index:D4}",
                    (ulong)(fileIndex * 100 + index),
                    (string?)$"f{fileIndex}-v{index}"))
                .ToArray()))
            .ToArray();

        AssertEquivalent(files, Plan(), targetSizeBytes: 4096);
    }

    static void AssertEquivalent(
        IReadOnlyList<SstContents> contents,
        CompactionPlan plan,
        long targetSizeBytes)
    {
        var expectedMerged = CompactionMerger.Merge(contents, plan);
        var expected = CompactionOutputPartitioner.Partition(expectedMerged, targetSizeBytes);

        using var directory = new TemporaryDirectory();
        var readers = contents
            .Select((content, index) => OpenReader(directory.Path, $"input-{index}.sst", content))
            .ToArray();
        try
        {
            var actual = StreamingCompactionMerger.MergeAndPartition(readers, plan, targetSizeBytes);

            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                AssertPartitionEqual(expected[index], actual[index]);
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    static void AssertPartitionEqual(CompactionMergeResult expected, CompactionMergeResult actual)
    {
        Assert.Equal(
            expected.Entries.Select(e => (Key: TestBytes.ToText(e.Key), e.Sequence, e.IsDelete)),
            actual.Entries.Select(e => (Key: TestBytes.ToText(e.Key), e.Sequence, e.IsDelete)));
        Assert.Equal(
            expected.Entries.Select(e => e.Value is null ? null : TestBytes.ToText(e.Value)),
            actual.Entries.Select(e => e.Value is null ? null : TestBytes.ToText(e.Value)));
        Assert.Equal(
            expected.RangeTombstones.Select(t => (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)),
            actual.RangeTombstones.Select(t => (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)));
    }

    static SstReader OpenReader(string directory, string fileName, SstContents content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(
            path,
            SstCodec.Encode(
                content.Entries.ToArray(),
                content.RangeTombstones.ToArray(),
                PantsPerformanceGoal.Latency));
        return SstReader.Open(path);
    }

    static CompactionPlan Plan(
        long? snapshotHorizon = null,
        bool pointTombstoneGcEligible = true,
        bool rangeTombstoneGcEligible = true) => new(
        0,
        1,
        0,
        snapshotHorizon,
        pointTombstoneGcEligible,
        rangeTombstoneGcEligible,
        []);

    static SstContents Build(params (string Key, ulong Sequence, string? Value)[] entries) =>
        Build(entries, []);

    static SstContents Build(
        (string Key, ulong Sequence, string? Value)[] entries,
        (string Start, string End, ulong Sequence)[] tombstones)
    {
        var sstEntries = entries
            .Select(e => new SstEntry(
                TestBytes.FromString(e.Key),
                e.Value is null ? null : TestBytes.FromString(e.Value),
                e.Sequence,
                null,
                e.Value is null))
            .ToArray();
        var rangeTombstones = tombstones
            .Select(t => new RangeTombstone(
                TestBytes.FromString(t.Start),
                TestBytes.FromString(t.End),
                t.Sequence))
            .ToArray();
        return new SstContents(sstEntries, rangeTombstones, 1);
    }
}
