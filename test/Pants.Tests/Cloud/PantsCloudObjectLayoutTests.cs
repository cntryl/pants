namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudObjectLayoutTests
{
    [Fact]
    public void ShouldAllowSegmentIdZeroGivenNonZeroWriterEpoch()
    {
        var objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(1, 0);

        Assert.Equal(
            "wal/epochs/00000000000000000001/00000000000000000000.wal",
            objectKey);
    }
}
