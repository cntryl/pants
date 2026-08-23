namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstStreamingTests
{
    [Theory]
    [InlineData(PantsPerformanceGoal.Latency)]
    [InlineData(PantsPerformanceGoal.Throughput)]
    [InlineData(PantsPerformanceGoal.Economy)]
    public void ShouldPreserveExactSstBytesGivenStreamingOutput(PantsPerformanceGoal goal)
    {
        var entries = Enumerable.Range(0, 256)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:D4}"),
                TestBytes.FromString($"value-{index:D4}"),
                checked((ulong)index + 1),
                index % 3 == 0 ? 1_000UL : null,
                false))
            .ToArray();
        var ranges = new[]
        {
            new RangeTombstone(TestBytes.FromString("key-0100"), TestBytes.FromString("key-0110"), 300)
        };
        var expected = SstCodec.Encode(entries, ranges, goal);
        using var destination = new MemoryStream();
        var checkedStream = new Crc32CWriteStream(destination);

        SstCodec.EncodeTo(checkedStream, entries, ranges, goal);

        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(expected.Length, checkedStream.BytesWritten);
        Assert.Equal(DiskFormat.Crc32C(expected), checkedStream.Checksum);
    }
}
