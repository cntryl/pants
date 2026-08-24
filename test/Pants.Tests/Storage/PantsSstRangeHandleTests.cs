using System.Buffers.Binary;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstRangeHandleTests
{
    [Fact]
    public void ShouldClassifyUnsupportedMetadataVersionAsCompatibilityFailure()
    {
        var metadata = BuildMetadataBytes(offset: 0, size: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            metadata,
            DiskFormat.SstFormatVersion + 1);

        Assert.Throws<PantsCompatibilityException>(() => SstCodec.DecodeMetadata(metadata));
    }

    [Fact]
    public void ShouldRejectRangeHandleGivenOffsetZeroAndSizePositive()
    {
        var metadata = BuildMetadataBytes(offset: 0, size: 16);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeMetadata(metadata));
    }

    [Fact]
    public void ShouldRejectRangeHandleGivenSizeZeroAndOffsetPositive()
    {
        var metadata = BuildMetadataBytes(offset: 16, size: 0);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeMetadata(metadata));
    }

    [Fact]
    public void ShouldDecodeAbsentRangeHandleGivenOffsetAndSizeBothZero()
    {
        var metadata = BuildMetadataBytes(offset: 0, size: 0);

        var decoded = SstCodec.DecodeMetadata(metadata);

        Assert.Null(decoded.RangeHandle);
    }

    [Fact]
    public void ShouldDecodePresentRangeHandleGivenOffsetAndSizeBothPositive()
    {
        var metadata = BuildMetadataBytes(offset: 8, size: 16);

        var decoded = SstCodec.DecodeMetadata(metadata);

        Assert.NotNull(decoded.RangeHandle);
        Assert.Equal(8UL, decoded.RangeHandle!.Value.Offset);
        Assert.Equal(16UL, decoded.RangeHandle!.Value.Size);
    }

    static byte[] BuildMetadataBytes(ulong offset, ulong size)
    {
        var metadata = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0), DiskFormat.SstFormatVersion);
        metadata[4] = 0; // index kind
        metadata[5] = 0; // flags: no key range
        metadata[6] = 0;
        metadata[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(metadata.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(metadata.AsSpan(16), size);
        return metadata;
    }
}
