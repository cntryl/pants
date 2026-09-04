using System.Buffers.Binary;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstLengthClassificationTests
{
    const uint UnsupportedLength = (uint)int.MaxValue + 1;

    [Fact]
    public void ShouldClassifyOversizedDataEntryValueLengthAsStorageFailure()
    {
        var block = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), UnsupportedLength);

        Assert.Throws<StorageException>(() =>
            SstCodec.DataBlockContainsKey(block, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedEncodedBlockLengthAsStorageFailure()
    {
        var file = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(file, UnsupportedLength);

        Assert.Throws<StorageException>(() =>
            SstCodec.ReadBlock(file, new SstBlockHandle(0, 9)));
    }

    [Fact]
    public void ShouldClassifyOversizedIndexKeyLengthAsStorageFailure()
    {
        var index = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(index, UnsupportedLength);

        Assert.Throws<StorageException>(() => SstCodec.DecodeIndex(index));
    }

    [Fact]
    public void ShouldClassifyOversizedBloomBlockCountAsStorageFailure()
    {
        var blooms = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(blooms, UnsupportedLength);

        Assert.Throws<StorageException>(() => SstCodec.ValidateBlockBlooms(blooms, 0));
        Assert.Throws<StorageException>(() => SstCodec.BloomMightContain(blooms, 0, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedBloomOffsetAsStorageFailure()
    {
        var blooms = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(blooms, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(blooms.AsSpan(sizeof(uint)), UnsupportedLength);

        Assert.Throws<StorageException>(() => SstCodec.ValidateBlockBlooms(blooms, 1));
        Assert.Throws<StorageException>(() => SstCodec.BloomMightContain(blooms, 0, "key"u8));
    }

    [Fact]
    public void ShouldClassifyOversizedMetadataKeyLengthAsStorageFailure()
    {
        var metadata = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata, DiskFormat.SstFormatVersion);
        metadata[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(24), UnsupportedLength);

        Assert.Throws<StorageException>(() => SstCodec.DecodeMetadata(metadata));
    }

    [Fact]
    public void ShouldClassifyOversizedRangeTombstoneCountAsStorageFailure()
    {
        var tombstones = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(tombstones, UnsupportedLength);

        Assert.Throws<StorageException>(() => SstCodec.DecodeRangeTombstones(tombstones));
    }
}
