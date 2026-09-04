using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalCorruptionCoverageTests
{
    [Theory]
    [InlineData((byte)'X', (byte)'W', (byte)1)]
    [InlineData((byte)'M', (byte)'W', (byte)2)]
    public void ShouldRejectRecordAndCrcValidFrameGivenInvalidEnvelopePrefix(
        byte firstMagic,
        byte secondMagic,
        byte version)
    {
        var payload = BuildRawRecord();
        payload[0] = firstMagic;
        payload[1] = secondMagic;
        payload[2] = version;
        var frame = BuildFrame(payload);

        Assert.Throws<StorageException>(() => WalCodec.DecodeRecord(payload));
        Assert.Throws<StorageException>(() => WalFrameReader.Visit(frame, static (_, _) => { }));
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)10)]
    public void ShouldRejectRecordGivenRequiredTlvIsMissing(byte omittedTag)
    {
        var payload = BuildRawRecord(omittedTag);
        var frame = BuildFrame(payload);

        Assert.Throws<StorageException>(() => WalCodec.DecodeRecord(payload));
        Assert.Throws<StorageException>(() => WalFrameReader.Visit(frame, static (_, _) => { }));
    }

    [Theory]
    [InlineData((byte)WalOperation.TransactionBegin)]
    [InlineData((byte)WalOperation.TransactionCommit)]
    [InlineData((byte)WalOperation.TransactionBatch)]
    public void ShouldRejectTransactionBatchGivenNestedTransactionMarker(byte operation)
    {
        var record = BuildBatchRecord(
            BuildBatchValue([((WalOperation)operation, 12UL)]),
            13);

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("operation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectTransactionBatchGivenNonContiguousNestedSequence()
    {
        var record = BuildBatchRecord(
            BuildBatchValue([
                (WalOperation.Put, 12UL),
                (WalOperation.Put, 14UL)
            ]),
            14);

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("not contiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectTransactionBatchGivenTrailingBytes()
    {
        var value = BuildBatchValue([(WalOperation.Put, 12UL)]).Append((byte)0xFF).ToArray();
        var record = BuildBatchRecord(value, 13);

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("trailing bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRejectTransactionBatchGivenOuterMetadataMismatch(bool mismatchTransactionId)
    {
        var record = BuildBatchRecord(
            BuildBatchValue([(WalOperation.Put, 12UL)]),
            13);
        record = mismatchTransactionId
            ? record with { TransactionId = 8 }
            : record with { Sequence = 14 };

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("metadata is inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldLeaveFileUnmodifiedGivenOversizedFrame()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.wal");
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var payload = new byte[DiskFormat.WalMaximumRecordBytes + 1];

        Assert.Throws<StorageException>(() => WalCodec.AppendFrame(handle, 0, payload));

        Assert.Equal(0, RandomAccess.GetLength(handle));
    }

    [Fact]
    public void ShouldLeaveFileUnmodifiedGivenOversizedFrameInGroup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized-group.wal");
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite);
        byte[][] payloads =
        [
            "valid"u8.ToArray(),
            new byte[DiskFormat.WalMaximumRecordBytes + 1]
        ];

        Assert.Throws<StorageException>(() => WalCodec.AppendFrames(handle, 0, payloads));

        Assert.Equal(0, RandomAccess.GetLength(handle));
    }

    static byte[] BuildRawRecord(byte? omittedTag = null)
    {
        using var payload = new MemoryStream();
        payload.Write("MW"u8);
        payload.WriteByte(1);
        WriteTlvUnlessOmitted(payload, omittedTag, 1, [(byte)WalOperation.Put]);
        WriteTlvUInt32UnlessOmitted(payload, omittedTag, 2, 1);
        WriteTlvUInt64UnlessOmitted(payload, omittedTag, 3, 5);
        WriteTlvUnlessOmitted(payload, omittedTag, 4, "key"u8);
        WriteTlvUnlessOmitted(payload, omittedTag, 5, "value"u8);
        WriteTlvUInt64UnlessOmitted(payload, omittedTag, 10, 9);
        return payload.ToArray();
    }

    static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[2 * sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(sizeof(uint)), DiskFormat.Crc32C(payload));
        payload.CopyTo(frame, 2 * sizeof(uint));
        return frame;
    }

    static byte[] BuildBatchValue(IReadOnlyList<(WalOperation Operation, ulong Sequence)> operations)
    {
        const ulong transactionId = 7;
        const ulong beginSequence = 11;
        var commitSequence = checked(beginSequence + (ulong)operations.Count + 1);
        using var batch = new MemoryStream();
        batch.Write("TB"u8);
        batch.WriteByte(1);
        DiskFormat.WriteUInt64(batch, transactionId);
        DiskFormat.WriteUInt64(batch, beginSequence);
        DiskFormat.WriteUInt64(batch, commitSequence);
        DiskFormat.WriteUInt32(batch, checked((uint)operations.Count));
        foreach (var (operation, sequence) in operations)
        {
            batch.WriteByte((byte)operation);
            DiskFormat.WriteUInt32(batch, 1);
            DiskFormat.WriteUInt64(batch, sequence);
            batch.WriteByte(0);
            DiskFormat.WriteUInt32(batch, 1);
            batch.WriteByte((byte)'k');
            batch.WriteByte(0);
            batch.WriteByte(0);
        }

        return batch.ToArray();
    }

    static WalRecord BuildBatchRecord(byte[] value, ulong commitSequence) => new(
        0,
        WalOperation.TransactionBatch,
        "txn"u8.ToArray(),
        value,
        commitSequence,
        null,
        null,
        7,
        9);

    static void WriteTlvUnlessOmitted(
        Stream stream,
        byte? omittedTag,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        if (omittedTag != tag)
        {
            WriteTlv(stream, tag, value);
        }
    }

    static void WriteTlvUInt32UnlessOmitted(Stream stream, byte? omittedTag, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteTlvUnlessOmitted(stream, omittedTag, tag, bytes);
    }

    static void WriteTlvUInt64UnlessOmitted(Stream stream, byte? omittedTag, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteTlvUnlessOmitted(stream, omittedTag, tag, bytes);
    }

    static void WriteTlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        DiskFormat.WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }
}
