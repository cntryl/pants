namespace Pants.Tests;

public sealed class SstReaderCacheTests
{
    [Fact]
    public void ShouldCacheParsedReaderAndOwnItsFileHandle()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "reader.sst");
        MidgeSstEntry[] entries = Enumerable.Range(0, 128)
            .Select(index => new MidgeSstEntry(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(
            path,
            MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        using var cache = new SstReaderCache();

        MidgeSstReader first = cache.GetOrAdd("reader.sst", path, out bool firstHit);
        MidgeSstReader second = cache.GetOrAdd("reader.sst", path, out bool secondHit);
        SstPointReadDecision decision = second.GetPointReadDecision(entries[64].Key);
        byte[] block = second.ReadDataBlock(decision.CandidateBlockIndex);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.True(MidgeSstCodec.DataBlockContainsKey(block, entries[64].Key));
        Assert.Equal(["reader.sst"], cache.SnapshotFiles());

        cache.RemoveFile("reader.sst");

        Assert.True(first.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
    }
}
