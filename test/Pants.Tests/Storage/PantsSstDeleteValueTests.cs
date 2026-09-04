namespace Cntryl.Pants.Storage;

public sealed class PantsSstDeleteValueTests
{
    [Fact]
    public void ShouldPreserveValueBytesGivenDeleteEntryCarriesReservedValue()
    {
        var expectedValue = "reserved-value"u8.ToArray();
        var bytes = SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), expectedValue, 7, null, true)],
            [],
            PantsPerformanceGoal.Latency);

        var decoded = Assert.Single(SstCodec.Decode(bytes).Entries);

        Assert.True(decoded.IsDelete);
        Assert.Equal(expectedValue, decoded.Value);
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
