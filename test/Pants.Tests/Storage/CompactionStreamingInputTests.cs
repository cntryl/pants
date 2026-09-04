namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 5 (issue #219), input side: compaction's input-reading step
/// (<c>LocalDiskStore.CompactAsync</c>) used to <c>PositionalFile.ReadAllBytes</c> then
/// <c>SstCodec.Decode</c> each input SST in full before merging — holding the whole file's bytes
/// and its fully-decoded contents in memory at once, per input. It now walks each input via
/// <see cref="SstBlockIterator"/> (one block resident at a time) into the same
/// <see cref="SstContents"/> shape <c>CompactionMerger.Merge</c> already consumes, so the merge
/// logic itself — version retention, tombstone-covers-entry, GC eligibility — is unchanged and
/// unrisked; only how the entries are assembled changes. (The merge/partition steps
/// downstream still materialize the full merged result — see the plan file for what remains.)
/// </summary>
public sealed class CompactionStreamingInputTests
{
    [Fact]
    public void ShouldProduceIdenticalContentsToWholeFileDecode()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries, tombstones) = CreateMultiBlockSstWithTombstones(directory.Path);
        var baseline = SstCodec.Decode(File.ReadAllBytes(path));

        using var reader = SstReader.Open(path);
        var streamed = SstCodec.DecodeViaBlockIterator(reader);

        Assert.Equal(baseline.Entries.Select(e => e.Key), streamed.Entries.Select(e => e.Key));
        Assert.Equal(baseline.Entries.Select(e => e.Sequence), streamed.Entries.Select(e => e.Sequence));
        Assert.Equal(baseline.Entries.Select(e => e.Value), streamed.Entries.Select(e => e.Value));
        Assert.Equal(
            baseline.RangeTombstones.Select(t => (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)),
            streamed.RangeTombstones.Select(t => (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)));
        Assert.Equal(baseline.DataBlockCount, streamed.DataBlockCount);
        Assert.NotEmpty(tombstones);
        Assert.Equal(entries.Length, streamed.Entries.Count);
    }

    static (string Path, SstEntry[] Entries, RangeTombstone[] Tombstones) CreateMultiBlockSstWithTombstones(
        string directory)
    {
        var path = Path.Combine(directory, "compaction-input.sst");
        var entries = Enumerable.Range(0, 200)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:D6}"),
                new byte[512],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        RangeTombstone[] tombstones =
        [
            new RangeTombstone(TestBytes.FromString("key-000010"), TestBytes.FromString("key-000020"), 500)
        ];
        File.WriteAllBytes(path, SstCodec.Encode(entries, tombstones, PantsPerformanceGoal.Latency));
        return (path, entries, tombstones);
    }
}
