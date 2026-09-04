using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstCorruptionCoverageTests
{
    [Fact]
    public void ShouldRejectTrieGivenCycleToRoot()
    {
        byte[] trie =
        [
            2,
            0, 0, 0, 1, (byte)'a', 1,
            0, 1, (byte)'a', 1, 1, (byte)'a', 0
        ];

        var exception = Assert.Throws<StorageException>(() => TrieIndex.Decode(trie, ["a"u8.ToArray()]));

        Assert.Contains("cyclic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectTrieGivenDisconnectedNode()
    {
        byte[] trie =
        [
            2,
            0, 0, 0, 0,
            0, 1, (byte)'a', 1, 0
        ];

        var exception = Assert.Throws<StorageException>(() => TrieIndex.Decode(trie, ["a"u8.ToArray()]));

        Assert.Contains("disconnected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((byte)'a', (byte)'a')]
    [InlineData((byte)'b', (byte)'a')]
    public void ShouldRejectTrieGivenDuplicateOrUnsortedChildEdges(byte first, byte second)
    {
        byte[] trie =
        [
            3,
            0, 0, 0, 2, first, 1, second, 2,
            0, 1, first, 1, 0,
            0, 1, second, 2, 0
        ];

        var exception = Assert.Throws<StorageException>(() => TrieIndex.Decode(trie, [[first], [second]]));

        Assert.Contains("duplicated or unsorted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectTrieGivenEmptyNonRootKeyDelta()
    {
        byte[] trie =
        [
            2,
            0, 0, 0, 1, (byte)'a', 1,
            0, 0, 1, 0
        ];

        var exception = Assert.Throws<StorageException>(() => TrieIndex.Decode(trie, ["a"u8.ToArray()]));

        Assert.Contains("empty non-root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16, (byte)5)]
    [InlineData(17, (byte)2)]
    public void ShouldRejectDataEntryGivenInvalidMetadataByte(int offset, byte value)
    {
        var block = BuildDataEntry();
        block[offset] = value;

        var exception = Assert.Throws<StorageException>(() => SstCodec.DataBlockContainsKey(block, "k"u8));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectDataEntryGivenExpirationBytesWithoutPresenceFlag()
    {
        var block = BuildDataEntry();
        block[18] = 1;

        var exception = Assert.Throws<StorageException>(() => SstCodec.DataBlockContainsKey(block, "k"u8));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ushort.MaxValue)]
    [InlineData(ushort.MaxValue + 1)]
    public void ShouldRoundTripEntryGivenKeyDeltaAtExtendedLengthBoundary(int keyLength)
    {
        var key = Enumerable.Repeat((byte)'k', keyLength).ToArray();
        var entry = new SstEntry(key, "value"u8.ToArray(), 1, null, false);

        var contents = SstCodec.Decode(SstCodec.Encode([entry], [], PantsPerformanceGoal.Latency));

        var decoded = Assert.Single(contents.Entries);
        Assert.Equal(key, decoded.Key);
        Assert.Equal(entry.Value, decoded.Value);
    }

    [Fact]
    public void ShouldRejectMetadataGivenUnknownFlagBit()
    {
        var metadata = BuildMetadata();
        metadata[5] = 0b0000_0010;

        var exception = Assert.Throws<StorageException>(() => SstCodec.DecodeMetadata(metadata));

        Assert.Contains("flags", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectMetadataWithoutKeyRangeGivenTrailingBytes()
    {
        var metadata = BuildMetadata().Append((byte)0).ToArray();

        var exception = Assert.Throws<StorageException>(() => SstCodec.DecodeMetadata(metadata));

        Assert.Contains("trailing bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectReaderIndexHandleGivenOffsetBeyondEndOfFile()
    {
        var bytes = BuildSst();
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[16..], checked((ulong)bytes.Length + 1));
        RecomputeFooterCrc(footer);

        var exception = OpenMalformedSst(bytes);

        Assert.Contains("outside the file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectReaderIndexHandleGivenSizeBelowMinimum()
    {
        var bytes = BuildSst();
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[24..], 3);
        RecomputeFooterCrc(footer);

        var exception = OpenMalformedSst(bytes);

        Assert.Contains("outside the file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static byte[] BuildDataEntry()
    {
        var block = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), 1);
        block[26] = (byte)'k';
        block[27] = (byte)'v';
        return block;
    }

    static byte[] BuildMetadata()
    {
        var metadata = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata, DiskFormat.SstFormatVersion);
        return metadata;
    }

    static byte[] BuildSst() => SstCodec.Encode(
        [new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)],
        [],
        PantsPerformanceGoal.Latency);

    static StorageException OpenMalformedSst(byte[] bytes)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "malformed.sst");
        File.WriteAllBytes(path, bytes);
        return Assert.Throws<StorageException>(() => SstReader.Open(path));
    }

    static void RecomputeFooterCrc(Span<byte> footer) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[80..],
            DiskFormat.Crc32C(footer[..80]));
}
