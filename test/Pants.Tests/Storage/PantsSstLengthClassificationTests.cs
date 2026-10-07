using System.Buffers.Binary;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstLengthClassificationTests
{
    const uint UnsupportedLength = (uint)int.MaxValue + 1;

    [Fact]
    public void ShouldClassifyOversizedDataEntryValueLengthAsCorruption()
    {
        var block = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() =>
            SstCodec.DataBlockContainsKey(block, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedEncodedBlockLengthAsCorruption()
    {
        var file = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(file, UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() =>
            SstCodec.ReadBlock(file, new SstBlockHandle(0, 9)));
    }

    [Fact]
    public void ShouldClassifyOversizedIndexKeyLengthAsCorruption()
    {
        var index = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(index, UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeIndex(index));
    }

    [Fact]
    public void ShouldClassifyOversizedBloomBlockCountAsCorruption()
    {
        var blooms = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(blooms, UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.ValidateBlockBlooms(blooms, 0));
        Assert.Throws<PantsCorruptionException>(() => SstCodec.BloomMightContain(blooms, 0, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedBloomOffsetAsCorruption()
    {
        var blooms = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(blooms, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(blooms.AsSpan(sizeof(uint)), UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.ValidateBlockBlooms(blooms, 1));
        Assert.Throws<PantsCorruptionException>(() => SstCodec.BloomMightContain(blooms, 0, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedMetadataKeyLengthAsCorruption()
    {
        var metadata = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata, DiskFormat.SstFormatVersion);
        metadata[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(24), UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeMetadata(metadata));
    }

    [Fact]
    public void ShouldClassifyOversizedRangeTombstoneCountAsCorruption()
    {
        var tombstones = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(tombstones, UnsupportedLength);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeRangeTombstones(tombstones));
    }
}
