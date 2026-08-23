namespace Cntryl.Pants.Tests.Storage;

public sealed class SstReaderCacheTests
{
    [Fact]
    public void ShouldCacheParsedReaderAndOwnItsFileHandle()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "reader.sst");
        var entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(
            path,
            SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        using var cache = new SstReaderCache();

        var first = cache.GetOrAdd("reader.sst", path, out var firstHit);
        var second = cache.GetOrAdd("reader.sst", path, out var secondHit);
        var decision = second.GetPointReadDecision(entries[64].Key);
        var block = second.ReadDataBlock(decision.CandidateBlockIndex);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.True(SstCodec.DataBlockContainsKey(block, entries[64].Key));
        Assert.Equal(["reader.sst"], cache.SnapshotFiles());

        cache.RemoveFile("reader.sst");

        Assert.True(first.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
    }
}
