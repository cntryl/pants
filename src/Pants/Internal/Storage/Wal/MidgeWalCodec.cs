using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Pants;

internal static class MidgeWalCodec
{
    private static ReadOnlySpan<byte> RecordMagic => "MW"u8;
    private static ReadOnlySpan<byte> BatchMagic => "TB"u8;

    public static byte[] EncodeTransactionBatch(ulong transactionId, ulong beginSequence, ulong writerEpoch, IReadOnlyList<MidgeWalMutation> mutations)
    {
        if (mutations.Count == 0)
        {
            throw new PantsStorageException("A Midge WAL transaction batch cannot be empty.");
        }

        var commitSequence = beginSequence + (ulong)mutations.Count + 1;
        using var batch = new MemoryStream();
        batch.Write(BatchMagic);
        batch.WriteByte(1);
        MidgeDiskFormat.WriteUInt64(batch, transactionId);
        MidgeDiskFormat.WriteUInt64(batch, beginSequence);
        MidgeDiskFormat.WriteUInt64(batch, commitSequence);
        MidgeDiskFormat.WriteUInt32(batch, (uint)mutations.Count);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index] with { Sequence = beginSequence + (ulong)index + 1 };
            batch.WriteByte((byte)mutation.Operation);
            MidgeDiskFormat.WriteUInt32(batch, mutation.ColumnFamilyId);
            MidgeDiskFormat.WriteUInt64(batch, mutation.Sequence);
            WriteOptionalUInt64(batch, mutation.Expiration);
            WriteBytes(batch, mutation.Key);
            WriteOptionalBytes(batch, mutation.Value);
            WriteOptionalBytes(batch, mutation.RangeEnd);
        }

        using var payload = new MemoryStream();
        payload.Write(RecordMagic);
        payload.WriteByte(1);
        WriteTlv(payload, 1, [(byte)MidgeWalOperation.TransactionBatch]);
        WriteTlvUInt32(payload, 2, 0);
        WriteTlvUInt64(payload, 3, commitSequence);
        WriteTlv(payload, 4, []);
        WriteTlvUInt64(payload, 10, writerEpoch);
        WriteTlv(payload, 5, batch.ToArray());
        WriteTlvUInt64(payload, 8, transactionId);
        return payload.ToArray();
    }

    public static IReadOnlyList<MidgeWalMutation> DecodeTransactionBatch(ReadOnlySpan<byte> payload, out ulong commitSequence)
    {
        if (payload.Length < 3 || !payload[..2].SequenceEqual(RecordMagic) || payload[2] != 1)
        {
            throw new PantsStorageException("WAL payload has an invalid Midge record prefix.");
        }

        byte? operation = null;
        ulong? outerSequence = null;
        ulong? outerTransactionId = null;
        byte[]? batchPayload = null;
        byte compression = 0;
        var cursor = 3;
        while (cursor < payload.Length)
        {
            if (payload.Length - cursor < 5)
            {
                throw new PantsStorageException("WAL TLV header is truncated.");
            }

            var tag = payload[cursor++];
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4)));
            cursor += 4;
            if (length > payload.Length - cursor)
            {
                throw new PantsStorageException("WAL TLV value is truncated.");
            }

            var value = payload.Slice(cursor, length);
            cursor += length;
            switch (tag)
            {
                case 1 when length == 1:
                    operation = value[0];
                    break;
                case 3 when length == 8:
                    outerSequence = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    break;
                case 5:
                    batchPayload = value.ToArray();
                    break;
                case 8 when length == 8:
                    outerTransactionId = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    break;
                case 9 when length == 1:
                    compression = value[0];
                    break;
            }
        }

        if (operation != (byte)MidgeWalOperation.TransactionBatch || batchPayload is null)
        {
            throw new PantsStorageException("Pants requires an atomic Midge WAL transaction batch.");
        }

        batchPayload = MidgeDiskFormat.Decompress(batchPayload, compression);
        var batch = batchPayload.AsSpan();
        if (batch.Length < 31 || !batch[..2].SequenceEqual(BatchMagic) || batch[2] != 1)
        {
            throw new PantsStorageException("WAL transaction batch prefix is invalid.");
        }

        var batchCursor = 3;
        var transactionId = ReadUInt64(batch, ref batchCursor, "transaction id");
        var beginSequence = ReadUInt64(batch, ref batchCursor, "begin sequence");
        commitSequence = ReadUInt64(batch, ref batchCursor, "commit sequence");
        var count = ReadUInt32(batch, ref batchCursor, "operation count");
        if (outerTransactionId != transactionId || outerSequence != commitSequence || commitSequence != beginSequence + count + 1)
        {
            throw new PantsStorageException("WAL transaction batch metadata is inconsistent.");
        }

        var mutations = new List<MidgeWalMutation>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var op = (MidgeWalOperation)ReadByte(batch, ref batchCursor, "operation");
            if (op is not (MidgeWalOperation.Put or MidgeWalOperation.Insert or MidgeWalOperation.Delete or MidgeWalOperation.DeleteRange))
            {
                throw new PantsStorageException($"WAL transaction batch operation '{op}' is invalid.");
            }

            var cfId = ReadUInt32(batch, ref batchCursor, "column family id");
            var sequence = ReadUInt64(batch, ref batchCursor, "sequence");
            if (sequence != beginSequence + index + 1)
            {
                throw new PantsStorageException("WAL transaction batch sequences are not contiguous.");
            }

            var expiration = ReadOptionalUInt64(batch, ref batchCursor, "expiration");
            var key = ReadBytes(batch, ref batchCursor, "key");
            var value = ReadOptionalBytes(batch, ref batchCursor, "value");
            var rangeEnd = ReadOptionalBytes(batch, ref batchCursor, "range end");
            mutations.Add(new MidgeWalMutation(cfId, op, key, value, sequence, expiration, rangeEnd));
        }

        if (batchCursor != batch.Length)
        {
            throw new PantsStorageException("WAL transaction batch has trailing bytes.");
        }

        return mutations;
    }

    public static void AppendFrame(
        SafeFileHandle handle,
        long offset,
        byte[] payload,
        Action? afterPartialPayload = null)
    {
        if (payload.Length > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw new PantsStorageException("WAL record exceeds Midge's 64 MiB frame limit.");
        }

        var header = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(sizeof(uint)),
            MidgeDiskFormat.Crc32C(payload));
        if (afterPartialPayload is null)
        {
            RandomAccess.Write(handle, [header, payload], offset);
            return;
        }

        int partialLength = Math.Max(1, payload.Length / 2);
        RandomAccess.Write(handle, [header, payload.AsMemory(0, partialLength)], offset);
        afterPartialPayload();
        RandomAccess.Write(
            handle,
            payload.AsSpan(partialLength),
            checked(offset + header.Length + partialLength));
    }

    private static void WriteTlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        MidgeDiskFormat.WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteTlvUInt32(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    private static void WriteTlvUInt64(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        MidgeDiskFormat.WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private static void WriteOptionalBytes(Stream stream, byte[]? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteBytes(stream, value);
        }
    }

    private static void WriteOptionalUInt64(Stream stream, ulong? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            MidgeDiskFormat.WriteUInt64(stream, value.Value);
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, 1, field);
        return data[cursor++];
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, 4, field);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, 4));
        cursor += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, 8, field);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, 8));
        cursor += 8;
        return value;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        var length = checked((int)ReadUInt32(data, ref cursor, field));
        EnsureAvailable(data, cursor, length, field);
        var value = data.Slice(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    private static byte[]? ReadOptionalBytes(ReadOnlySpan<byte> data, ref int cursor, string field) =>
        ReadFlag(data, ref cursor, field) ? ReadBytes(data, ref cursor, field) : null;

    private static ulong? ReadOptionalUInt64(ReadOnlySpan<byte> data, ref int cursor, string field) =>
        ReadFlag(data, ref cursor, field) ? ReadUInt64(data, ref cursor, field) : null;

    private static bool ReadFlag(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        var flag = ReadByte(data, ref cursor, field);
        return flag switch
        {
            0 => false,
            1 => true,
            _ => throw new PantsStorageException($"WAL {field} flag is invalid.")
        };
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int cursor, int length, string field)
    {
        if (length < 0 || cursor > data.Length - length)
        {
            throw new PantsStorageException($"WAL transaction batch {field} is truncated.");
        }
    }
}
