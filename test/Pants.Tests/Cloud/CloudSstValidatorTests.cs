namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudSstValidatorTests
{
    [Fact]
    public void ShouldRejectManifestSstMetadataGivenPhysicalKeyRangeDoesNotMatch()
    {
        var bytes = MidgeSstCodec.Encode(
            [new MidgeSstEntry("middle"u8.ToArray(), "value"u8.ToArray(), 4, null, false)],
            [],
            PantsPerformanceGoal.Latency);
        var file = new MidgeFileMeta
        {
            Name = "000000_00_00000000000000000001.sst",
            SizeBytes = checked((ulong)bytes.Length),
            ContentCrc32C = MidgeDiskFormat.Crc32C(bytes),
            ColumnFamilyId = 0,
            SmallestKey = "alpha"u8.ToArray().Select(static value => (int)value).ToArray(),
            LargestKey = "zulu"u8.ToArray().Select(static value => (int)value).ToArray(),
            SmallestSequence = 4,
            LargestSequence = 4
        };

        Assert.Throws<PantsCorruptionException>(() => CloudSstValidator.Validate(bytes, file));
    }
}
