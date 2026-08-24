using System.Buffers.Binary;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstBlockCoverageTests
{
    [Fact]
    public void ShouldRejectUnreferencedBytesBetweenSstBlocks()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 21,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(11, 10)));

        Assert.Equal("SST block references leave unreferenced bytes.", exception.Message);
    }

    [Fact]
    public void ShouldRejectOverlappingSstBlocks()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 19,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(9, 10)));

        Assert.Equal("SST block references overlap.", exception.Message);
    }

    [Fact]
    public void ShouldRejectSstBlocksThatDoNotExactlyReachFooter()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 20,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(10, 9)));

        Assert.Equal("SST block references do not exactly reach the footer.", exception.Message);
    }

    [Fact]
    public void ShouldRejectIndexFirstKeysThatDescend()
    {
        var index = EncodeIndexEntry("second"u8, new SstBlockHandle(10, 10))
            .Concat(EncodeIndexEntry("first"u8, new SstBlockHandle(0, 10)))
            .ToArray();

        var exception = Assert.Throws<StorageException>(() => SstCodec.DecodeIndex(index));

        Assert.Equal("SST index first keys are not sorted in ascending order.", exception.Message);
    }

    static void Validate(
        long footerOffset,
        SstBlockHandle metadata,
        SstBlockHandle index) =>
        SstCodec.ValidateBlockCoverage(
            footerOffset,
            metadata,
            index,
            trie: null,
            bloom: null,
            range: null,
            []);

    static byte[] EncodeIndexEntry(ReadOnlySpan<byte> key, SstBlockHandle handle)
    {
        var entry = new byte[sizeof(uint) + key.Length + 2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, checked((uint)key.Length));
        key.CopyTo(entry.AsSpan(sizeof(uint)));
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(sizeof(uint) + key.Length), handle.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            entry.AsSpan(sizeof(uint) + key.Length + sizeof(ulong)),
            handle.Size);
        return entry;
    }
}
