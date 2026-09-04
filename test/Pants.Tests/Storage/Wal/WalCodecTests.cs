using System.Buffers.Binary;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalCodecTests
{
    [Fact]
    public void ShouldRoundTripCurrentMidgeCompressedTransactionBatchOuterRecord()
    {
        var mutations = new[]
        {
            new WalMutation(
                1,
                WalOperation.Put,
                "key"u8.ToArray(),
                new byte[512],
                0,
                null,
                null)
        };

        var encoded = WalCodec.EncodeTransactionBatch(7, 11, 9, mutations);

        var decoded = WalCodec.DecodeTransactionBatch(encoded, out var commitSequence, out var writerEpoch);

        Assert.True(
            HasTopLevelTlvTag(encoded, 9),
            "Current Midge compresses sufficiently large TxnBatch outer values.");
        Assert.Equal<ulong>(13, commitSequence);
        Assert.Equal<ulong>(9, writerEpoch);
        var mutation = Assert.Single(decoded);
        Assert.Equal(new byte[512], mutation.Value);
    }

    [Fact]
    public void ShouldRejectDeleteMutationGivenValueIsPresent()
    {
        var record = new WalRecord(
            1,
            WalOperation.Delete,
            "key"u8.ToArray(),
            "corrupt-value"u8.ToArray(),
            5,
            null,
            null,
            null,
            9);

        var exception = Assert.Throws<StorageException>(() => WalCodec.DecodeMutation(record));

        Assert.Contains("Delete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldFailToDecodeDeleteMutationGivenValueIsPresent()
    {
        var record = new WalRecord(
            1,
            WalOperation.Delete,
            "key"u8.ToArray(),
            "corrupt-value"u8.ToArray(),
            5,
            null,
            null,
            null,
            9);

        var decoded = WalCodec.TryDecodeMutation(record, out var mutation);

        Assert.False(decoded);
        Assert.Null(mutation);
    }

    [Fact]
    public void ShouldRejectRecordGivenUnrecognizedCompressionAlgorithm()
    {
        var payload = EncodeRawRecordWithCompression(
            WalOperation.Put,
            "key"u8.ToArray(),
            "value"u8.ToArray(),
            99);

        var exception = Assert.Throws<PantsCorruptionException>(() => WalCodec.DecodeRecord(payload));

        Assert.Contains("compression", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static byte[] EncodeRawRecordWithCompression(
        WalOperation operation,
        byte[] key,
        byte[] value,
        byte compressionAlgorithm)
    {
        using var payload = new MemoryStream();
        payload.Write("MW"u8);
        payload.WriteByte(1);
        WriteTlv(payload, 1, [(byte)operation]);
        WriteTlvUInt32(payload, 2, 1);
        WriteTlvUInt64(payload, 3, 5);
        WriteTlv(payload, 4, key);
        WriteTlvUInt64(payload, 10, 9);
        WriteTlv(payload, 5, value);
        WriteTlv(payload, 9, [compressionAlgorithm]);
        return payload.ToArray();
    }

    static void WriteTlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)value.Length));
        stream.Write(length);
        stream.Write(value);
    }

    static void WriteTlvUInt32(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static void WriteTlvUInt64(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static bool HasTopLevelTlvTag(ReadOnlySpan<byte> payload, byte tag)
    {
        var cursor = 3;
        while (cursor < payload.Length)
        {
            var currentTag = payload[cursor++];
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            if (currentTag == tag)
            {
                return true;
            }

            cursor += length;
        }

        return false;
    }

    [Fact]
    public void ShouldRejectTransactionBatchGivenEmptyOperationCount()
    {
        var record = CreateBatch(0, []);

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("empty operation count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectTransactionBatchBeforeAllocationGivenHugeCountAndNoRecords()
    {
        var record = CreateBatch(10_000_000, []);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Contains(
            "operation count exceeds remaining bytes",
            exception.Message,
            StringComparison.Ordinal);
        Assert.InRange(allocatedBytes, 0L, 1_048_576L);
    }

    [Fact]
    public void ShouldRejectTransactionBatchBeforeDecodingGivenRecordBelowExactMinimumLength()
    {
        var record = CreateBatch(1, new byte[19]);

        var exception = Assert.Throws<StorageException>(() =>
            WalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains(
            "operation count exceeds remaining bytes",
            exception.Message,
            StringComparison.Ordinal);
    }

    static WalRecord CreateBatch(uint operationCount, byte[] records)
    {
        const ulong transactionId = 7;
        const ulong beginSequence = 11;
        var commitSequence = beginSequence + operationCount + 1;
        using var batch = new MemoryStream();
        batch.Write("TB"u8);
        batch.WriteByte(1);
        DiskFormat.WriteUInt64(batch, transactionId);
        DiskFormat.WriteUInt64(batch, beginSequence);
        DiskFormat.WriteUInt64(batch, commitSequence);
        DiskFormat.WriteUInt32(batch, operationCount);
        batch.Write(records);

        return new WalRecord(
            0,
            WalOperation.TransactionBatch,
            "txn"u8.ToArray(),
            batch.ToArray(),
            commitSequence,
            null,
            null,
            transactionId,
            9);
    }
}
