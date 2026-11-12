namespace Cntryl.Pants.Storage;

public sealed class PantsSstDeleteValueTests
{
    [Fact]
    public void ShouldRejectValueBytesGivenDeleteEntryCarriesValue()
    {
        Assert.Throws<PantsCorruptionException>(() => SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), "reserved-value"u8.ToArray(), 7, null, true)],
            [],
            PantsPerformanceGoal.Latency));
    }

    [Fact]
    public void ShouldKeepNullValueGivenDeleteEntryCarriesZeroLengthValue()
    {
        var bytes = SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), null, 7, null, true)],
            [],
            PantsPerformanceGoal.Latency);

        var decoded = Assert.Single(SstCodec.Decode(bytes).Entries);

        Assert.True(decoded.IsDelete);
        Assert.Null(decoded.Value);
    }
}
