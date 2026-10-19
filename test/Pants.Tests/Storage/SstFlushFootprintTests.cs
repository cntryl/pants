namespace Cntryl.Pants.Storage;

public sealed class SstFlushFootprintTests
{
    [Fact]
    public void ShouldComputeSstKeyBoundsFromUnsortedPointsAndRangeTombstones()
    {
        var entries = new[]
        {
            new SstEntry("point-z"u8.ToArray(), "z"u8.ToArray(), 9, null, false),
            new SstEntry("point-a"u8.ToArray(), "a"u8.ToArray(), 4, null, false)
        };
        var ranges = new[]
        {
            new RangeTombstone("range-m"u8.ToArray(), "range-x"u8.ToArray(), 7)
        };

        var (smallestKey, largestKey) = SstCodec.GetKeyBounds(entries, ranges);

        Assert.True(smallestKey is not null && "point-a"u8.SequenceEqual(smallestKey));
        Assert.True(largestKey is not null && "range-x"u8.SequenceEqual(largestKey));

        var contents = SstCodec.Decode(SstCodec.Encode(
            entries,
            ranges,
            PantsPerformanceGoal.Latency));

        Assert.Equal(2, contents.Entries.Count);
        Assert.Single(contents.RangeTombstones);
    }

    [Theory]
    [InlineData(PantsPerformanceGoal.Latency)]
    [InlineData(PantsPerformanceGoal.Throughput)]
    [InlineData(PantsPerformanceGoal.Economy)]
    public void ShouldBoundRangeOnlySst(PantsPerformanceGoal goal)
    {
        var start = System.Text.Encoding.UTF8.GetBytes("range-start");
        var end = System.Text.Encoding.UTF8.GetBytes("range-end");
        var tombstone = new RangeTombstone(start, end, 1);
        var mutation = new WalMutation(
            0,
            WalOperation.DeleteRange,
            start,
            null,
            1,
            null,
            end);

        var encoded = SstCodec.Encode([], [tombstone], goal);

        Assert.True(encoded.LongLength <=
                    SstFlushFootprint.MeasureUpperBound([mutation], goal));
    }

    [Theory]
    [InlineData(PantsPerformanceGoal.Latency)]
    [InlineData(PantsPerformanceGoal.Throughput)]
    [InlineData(PantsPerformanceGoal.Economy)]
    public void ShouldBoundEncodedSstForSparseTrieAndRangeWorkloads(PantsPerformanceGoal goal)
    {
        var random = new Random(163);
        var entries = new List<SstEntry>();
        var mutations = new List<WalMutation>();
        for (var index = 0; index < 256; index++)
        {
            var key = index < 128
                ? System.Text.Encoding.UTF8.GetBytes($"tenant/shared-prefix/{index:D6}")
                : RandomBytes(random, 64 + index % 17);
            var value = RandomBytes(random, 512 + index % 31);
            var sequence = checked((ulong)index + 1);
            entries.Add(new SstEntry(key, value, sequence, null, false));
            mutations.Add(new WalMutation(
                0,
                WalOperation.Put,
                key,
                value,
                sequence,
                null,
                null));
        }

        var longKey = RandomBytes(random, 32 * 1024);
        var longValue = RandomBytes(random, 16 * 1024);
        entries.Add(new SstEntry(longKey, longValue, 257, null, false));
        mutations.Add(new WalMutation(0, WalOperation.Put, longKey, longValue, 257, null, null));

        var ranges = new List<RangeTombstone>();
        for (var index = 0; index < 8; index++)
        {
            var start = System.Text.Encoding.UTF8.GetBytes($"range-{index:D2}");
            var end = System.Text.Encoding.UTF8.GetBytes($"range-{index:D2}-end");
            var sequence = checked((ulong)index + 258);
            ranges.Add(new RangeTombstone(start, end, sequence));
            mutations.Add(new WalMutation(
                0,
                WalOperation.DeleteRange,
                start,
                null,
                sequence,
                null,
                end));
        }

        var encoded = SstCodec.Encode(entries, ranges, goal);

        Assert.True(encoded.LongLength <= SstFlushFootprint.MeasureUpperBound(mutations, goal));
    }

    static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
