namespace Cntryl.Pants.Tests.Storage;

public sealed class SstBlockIteratorTests
{
    [Fact]
    public void ShouldIterateForwardMatchingFullDecodeBaseline()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateMultiBlockSst(directory.Path);
        using var reader = SstReader.Open(path);

        using var iterator = SstBlockIterator.Create(reader, PantsScanDirection.Forward);
        var observed = Drain(iterator);

        Assert.Equal(entries.Select(entry => entry.Key), observed.Select(entry => entry.Key));
        Assert.Equal(entries.Select(entry => entry.Sequence), observed.Select(entry => entry.Sequence));
    }

    [Fact]
    public void ShouldIterateReverseMatchingFullDecodeBaselineReversed()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateMultiBlockSst(directory.Path);
        using var reader = SstReader.Open(path);

        using var iterator = SstBlockIterator.Create(reader, PantsScanDirection.Reverse);
        var observed = Drain(iterator);

        Assert.Equal(
            entries.Reverse().Select(entry => entry.Key),
            observed.Select(entry => entry.Key));
    }

    [Fact]
    public void ShouldHonorLowerBoundByBinarySearchingIntoAMiddleBlock()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateMultiBlockSst(directory.Path);
        using var reader = SstReader.Open(path);
        var lowerBound = entries[entries.Length / 2].Key;

        using var iterator = SstBlockIterator.Create(
            reader,
            PantsScanDirection.Forward,
            startInclusive: lowerBound);
        var observed = Drain(iterator);

        var expected = entries.Where(entry => entry.Key.AsSpan().SequenceCompareTo(lowerBound) >= 0);
        Assert.Equal(expected.Select(entry => entry.Key), observed.Select(entry => entry.Key));
    }

    [Fact]
    public void ShouldHonorUpperBoundExclusiveInReverse()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateMultiBlockSst(directory.Path);
        using var reader = SstReader.Open(path);
        var upperBound = entries[entries.Length / 2].Key;

        using var iterator = SstBlockIterator.Create(
            reader,
            PantsScanDirection.Reverse,
            endExclusive: upperBound);
        var observed = Drain(iterator);

        var expected = entries
            .Where(entry => entry.Key.AsSpan().SequenceCompareTo(upperBound) < 0)
            .Reverse();
        Assert.Equal(expected.Select(entry => entry.Key), observed.Select(entry => entry.Key));
    }

    [Fact]
    public void ShouldNotRetainMoreThanOneDecodedBlockAtATimeAsFileGrows()
    {
        using var directory = new TemporaryDirectory();
        var (smallPath, _) = CreateMultiBlockSst(directory.Path, blockCount: 4, entriesPerBlock: 8);
        var (largePath, _) = CreateMultiBlockSst(directory.Path, blockCount: 64, entriesPerBlock: 8, fileName: "large.sst");

        var smallPeakBlockBytes = MeasurePeakDecodedBlockBytes(smallPath);
        var largePeakBlockBytes = MeasurePeakDecodedBlockBytes(largePath);

        Assert.True(
            largePeakBlockBytes <= smallPeakBlockBytes * 4,
            $"Expected peak decoded-block footprint to stay roughly constant as file size grew " +
            $"(small={smallPeakBlockBytes}, large={largePeakBlockBytes}).");
    }

    [Fact]
    public void ShouldExposeRangeTombstonesWithoutAdvancingTheIterator()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "tombstones.sst");
        var entries = new[]
        {
            new SstEntry(TestBytes.FromString("key-0001"), new byte[16], 1, null, false),
            new SstEntry(TestBytes.FromString("key-0002"), new byte[16], 2, null, false)
        };
        RangeTombstone[] tombstones =
        [
            new RangeTombstone(
                TestBytes.FromString("key-0000"),
                TestBytes.FromString("key-0003"),
                5)
        ];
        File.WriteAllBytes(path, SstCodec.Encode(entries, tombstones, PantsPerformanceGoal.Latency));
        using var reader = SstReader.Open(path);

        var exposed = reader.RangeTombstones;

        Assert.Single(exposed);
        Assert.Equal(5UL, exposed[0].Sequence);
    }

    [Fact]
    public void ShouldReleaseTheFinalDecodedBlockAsSoonAsItIsExhausted()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateMultiBlockSst(directory.Path, blockCount: 1);
        using var reader = SstReader.Open(path);
        var budget = new ResourceBudget(1024 * 1024);
        using var iterator = SstBlockIterator.Create(
            reader,
            PantsScanDirection.Forward,
            resourceBudget: budget);

        Assert.True(iterator.MoveNext());
        Assert.True(budget.Current > 0);
        while (iterator.MoveNext())
        {
        }

        Assert.Equal(0, budget.Current);
    }

    static long MeasurePeakDecodedBlockBytes(string path)
    {
        using var reader = SstReader.Open(path);
        using var iterator = SstBlockIterator.Create(reader, PantsScanDirection.Forward);
        long peak = 0;
        while (iterator.MoveNext())
        {
            peak = Math.Max(peak, iterator.CurrentBlockBytes);
        }

        return peak;
    }

    static List<SstEntry> Drain(SstBlockIterator iterator)
    {
        var results = new List<SstEntry>();
        while (iterator.MoveNext())
        {
            results.Add(iterator.Current);
        }

        return results;
    }

    static (string Path, SstEntry[] Entries) CreateMultiBlockSst(
        string directory,
        int blockCount = 8,
        int entriesPerBlock = 16,
        string fileName = "iterator.sst")
    {
        var path = Path.Combine(directory, fileName);
        var total = blockCount * entriesPerBlock;
        var entries = Enumerable.Range(0, total)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:D6}"),
                new byte[4096],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(
            path,
            SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        return (path, entries);
    }
}
